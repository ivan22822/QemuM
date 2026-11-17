using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// One worker owns the sequential transport. Priority never interrupts a sent command.
sealed class BackendRequestQueue : IDisposable {
    sealed class Waiter {
        internal readonly TaskCompletionSource<object> Source=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
    }
    sealed class Request {
        internal string Key;internal bool Background,Started;internal long Deadline;
        internal Func<object> Work;internal readonly List<Waiter> Waiters=new List<Waiter>();
    }
    readonly object gate=new object();readonly List<Request> waiting=new List<Request>();
    readonly Dictionary<string,Request> keyed=new Dictionary<string,Request>(StringComparer.Ordinal);
    readonly Timer expiry;readonly int capacity;bool running,closed;long generation;Action close;
    internal BackendRequestQueue(int capacity=64){this.capacity=capacity;expiry=new Timer(Expire,null,Timeout.Infinite,Timeout.Infinite);}
    internal int PendingCount{get{lock(gate)return waiting.Count;}}
    internal bool HasInteractivePending{get{lock(gate)return waiting.Any(r=>!r.Background);}}
    static long Now{get{return Stopwatch.GetTimestamp();}}
    void Arm(){
        if(closed||waiting.Count==0){expiry.Change(Timeout.Infinite,Timeout.Infinite);return;}
        double ms=(waiting.Min(r=>r.Deadline)-Now)*1000.0/Stopwatch.Frequency;
        expiry.Change((int)Math.Min(Int32.MaxValue,Math.Max(1,Math.Ceiling(ms))),Timeout.Infinite);
    }
    void Forget(Request request){if(request.Key!=null){Request existing;if(keyed.TryGetValue(request.Key,out existing)&&Object.ReferenceEquals(existing,request))keyed.Remove(request.Key);}}
    internal Task<object> Enqueue(string key,bool readOnly,bool background,Func<object> work,int timeout,CancellationToken cancellation){
        if(!readOnly&&cancellation.CanBeCanceled)throw new InvalidOperationException("Операцию записи нельзя отменить как чтение.");
        if(cancellation.IsCancellationRequested){var cancelled=new TaskCompletionSource<object>();cancelled.SetCanceled();return cancelled.Task;}
        var waiter=new Waiter();Request request;
        lock(gate){
            if(closed){waiter.Source.SetException(new ObjectDisposedException("Backend"));return waiter.Source.Task;}
            // A read submitted after a write must not join a pre-write response.
            if(!readOnly)generation++;
            string versioned=readOnly&&key!=null?generation+":"+key:null;
            if(versioned==null||!keyed.TryGetValue(versioned,out request)){
                if(waiting.Count>=capacity){waiter.Source.SetException(new InvalidOperationException("Очередь службы заполнена. Повтори запрос после завершения текущих операций."));return waiter.Source.Task;}
                request=new Request{Key=versioned,Background=readOnly&&background,Work=work,Deadline=Now+(long)(Math.Max(1,timeout)*Stopwatch.Frequency/1000.0)};
                waiting.Add(request);if(versioned!=null)keyed[versioned]=request;
            }else if(!background)request.Background=false;
            request.Waiters.Add(waiter);Arm();
            if(!running){running=true;Task.Run((Action)Drain);}
        }
        if(cancellation.CanBeCanceled){
            waiter.Registration=cancellation.Register(()=>Cancel(request,waiter));
            if(waiter.Source.Task.IsCompleted)waiter.Registration.Dispose();
        }
        return waiter.Source.Task;
    }
    void Cancel(Request request,Waiter waiter){
        lock(gate){
            if(!request.Waiters.Remove(waiter))return;
            if(request.Waiters.Count==0&&!request.Started){waiting.Remove(request);Forget(request);Arm();}
        }
        waiter.Source.TrySetCanceled();
    }
    static void Complete(IEnumerable<Waiter> waiters,object value,Exception error){
        foreach(var waiter in waiters){waiter.Registration.Dispose();if(error==null)waiter.Source.TrySetResult(value);else waiter.Source.TrySetException(error);}
    }
    void Expire(object ignored){
        var expired=new List<Waiter>();
        lock(gate){
            foreach(var request in waiting.Where(r=>r.Deadline<=Now).ToArray()){
                waiting.Remove(request);Forget(request);expired.AddRange(request.Waiters);request.Waiters.Clear();
            }
            if(!closed)Arm();
        }
        Complete(expired,null,new TimeoutException("Фоновая служба занята другим запросом. Повтори чтение через несколько секунд."));
    }
    void Drain(){
        for(;;){
            Request request=null;Action finish=null;
            lock(gate){
                if(waiting.Count==0){running=false;if(closed){finish=close;close=null;}else Arm();}
                else{
                    request=waiting.FirstOrDefault(r=>!r.Background)??waiting[0];waiting.Remove(request);request.Started=true;Arm();
                }
            }
            if(request==null){if(finish!=null)finish();return;}
            object value=null;Exception error=null;
            try{
                if(request.Deadline<Now)throw new TimeoutException("Истекло время ожидания запроса в очереди.");
                if(request.Waiters.Count>0)value=request.Work();
            }catch(Exception failure){error=failure;}
            Waiter[] completed;
            lock(gate){Forget(request);completed=request.Waiters.ToArray();request.Waiters.Clear();}
            Complete(completed,value,error);
        }
    }
    internal void Stop(Action finish){
        var abandoned=new List<Waiter>();bool idle;
        lock(gate){
            if(closed)return;closed=true;close=finish;expiry.Change(Timeout.Infinite,Timeout.Infinite);
            foreach(var request in waiting){Forget(request);abandoned.AddRange(request.Waiters);request.Waiters.Clear();}waiting.Clear();
            idle=!running;if(idle)close=null;
        }
        expiry.Dispose();Complete(abandoned,null,new ObjectDisposedException("Backend"));
        if(idle&&finish!=null)Task.Run(finish);
    }
    public void Dispose(){Stop(null);}
}
