using System;using System.IO;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using System.Diagnostics;
static class RequestQueueTests {
 static int checks;static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
 static object Result(Task<object> task){Check(task.Wait(5000),"request completes within test deadline");return task.GetAwaiter().GetResult();}
 static void Fault<T>(Task<object> task,string message)where T:Exception{try{task.GetAwaiter().GetResult();throw new Exception("Expected "+typeof(T).Name+": "+message);}catch(T){checks++;}}
 [STAThread]static int Main(string[] args){try{
  using(var queue=new BackendRequestQueue())using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim()){
   var order=new List<string>();
   var first=queue.Enqueue("first",true,true,()=>{entered.Set();release.Wait(5000);order.Add("first");return 1;},5000,CancellationToken.None);
   Check(entered.Wait(3000),"first background request has started");
   var background=queue.Enqueue("poll",true,true,()=>{order.Add("poll");return 2;},5000,CancellationToken.None);
   var user=queue.Enqueue("user",true,false,()=>{order.Add("user");return 3;},5000,CancellationToken.None);
   var write=queue.Enqueue(null,false,false,()=>{order.Add("write");return 4;},5000,CancellationToken.None);
   Check(queue.PendingCount==3&&queue.HasInteractivePending,"queue reports bounded waiting work");
   Check(!first.IsCompleted&&!user.IsCompleted,"priority does not interrupt the active request");release.Set();
   Result(first);Result(user);Result(write);Result(background);
   Check(String.Join(",",order)=="first,user,write,poll","interactive requests overtake only pending background reads; writes stay ordered");
  }
  using(var queue=new BackendRequestQueue())using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cancelled=new CancellationTokenSource()){
   int calls=0;
   var one=queue.Enqueue("same",true,true,()=>{Interlocked.Increment(ref calls);entered.Set();release.Wait(5000);return 9;},5000,cancelled.Token);
   Check(entered.Wait(3000),"shared read has started");
   var two=queue.Enqueue("same",true,false,()=>{throw new Exception("duplicate read executed");},5000,CancellationToken.None);
   cancelled.Cancel();Fault<OperationCanceledException>(one,"one subscriber can cancel independently");release.Set();
   Check((int)Result(two)==9&&calls==1,"another subscriber still receives the single transport result");
  }
  using(var queue=new BackendRequestQueue())using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim()){
   int version=0;
   var before=queue.Enqueue("version",true,true,()=>{entered.Set();release.Wait(5000);return version;},5000,CancellationToken.None);
   Check(entered.Wait(3000),"pre-write read starts");
   var write=queue.Enqueue(null,false,false,()=>{version=1;return 1;},5000,CancellationToken.None);
   var after=queue.Enqueue("version",true,false,()=>version,5000,CancellationToken.None);release.Set();
   Check((int)Result(before)==0&&(int)Result(write)==1&&(int)Result(after)==1,"post-write reads do not coalesce into pre-write responses");
  }
  using(var queue=new BackendRequestQueue(2))using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cancel=new CancellationTokenSource()){
   var active=queue.Enqueue("active",true,true,()=>{entered.Set();release.Wait(5000);return 1;},5000,CancellationToken.None);Check(entered.Wait(3000),"capacity fixture starts");
   int discardedCalls=0;
   var removed=queue.Enqueue("removed",true,true,()=>{discardedCalls++;return 1;},5000,cancel.Token);
   var expired=queue.Enqueue("expired",true,true,()=>{discardedCalls++;return 1;},40,CancellationToken.None);
   Fault<InvalidOperationException>(queue.Enqueue("overflow",true,false,()=>1,5000,CancellationToken.None),"queue has a fixed capacity");
   cancel.Cancel();Fault<OperationCanceledException>(removed,"cancel removes queued read");
   Check(SpinWait.SpinUntil(()=>expired.IsCompleted,2000),"queued deadline fires while transport remains busy");Fault<TimeoutException>(expired,"queued read expires");
   Check(queue.PendingCount==0,"expired/cancelled requests leave the queue");release.Set();Result(active);Check(discardedCalls==0,"cancelled/expired reads are never submitted");
  }
  using(var queue=new BackendRequestQueue())using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var closed=new ManualResetEventSlim()){
   int writes=0;
   var active=queue.Enqueue(null,false,false,()=>{entered.Set();release.Wait(5000);Interlocked.Increment(ref writes);return 1;},5000,CancellationToken.None);Check(entered.Wait(3000),"write is in flight");
   var waiting=queue.Enqueue("later",true,false,()=>1,5000,CancellationToken.None);var elapsed=Stopwatch.StartNew();queue.Stop(()=>closed.Set());
   Check(elapsed.ElapsedMilliseconds<500&&!closed.IsSet,"shutdown returns without interrupting the active write");Fault<ObjectDisposedException>(waiting,"unsent work is rejected on shutdown");
   release.Set();Result(active);Check(closed.Wait(3000)&&writes==1,"transport closes after one write, without replay");
  }
  File.WriteAllText(Path.Combine(args[0],"request-queue-results.json"),"{\"passed\":"+checks+"}");return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"request-queue-error.txt"),error.ToString());return 1;}}
}
