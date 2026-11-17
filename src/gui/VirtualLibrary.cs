using System;using System.Collections.Generic;using System.Drawing;using System.Linq;using System.Windows.Forms;

interface IVirtualScrollContent {
 bool VirtualMode{get;}int ContentHeight(int width);void SetViewport(int width,int height,int offset);
}
sealed class VirtualLibraryRow {
 internal string Key,Version,AccessibleName,ParentKey;internal Padding Margin;internal object Tag;internal int EstimatedHeight=57;
 internal Func<Control> Create;internal Func<int,int> Measure;internal Action<Control> Bind;internal Action Activate,Expand;internal Func<bool> Expanded;
}
// The model contains every row; only the viewport owns native controls.
sealed class VirtualLibraryPanel : FlowLayoutPanel,IVirtualScrollContent {
 readonly Dictionary<string,Control> shown=new Dictionary<string,Control>(StringComparer.Ordinal);
 readonly Dictionary<string,string> versions=new Dictionary<string,string>(StringComparer.Ordinal);
 sealed class Measurement{internal string Version;internal int Width,Height;}
 readonly Dictionary<string,Measurement> measurements=new Dictionary<string,Measurement>(StringComparer.Ordinal);
 readonly Font machineFont=UiTypography.Make(11,400),standFont=UiTypography.Make(11,500);
 List<VirtualLibraryRow> model=new List<VirtualLibraryRow>();int[] positions=new int[1];int measuredWidth=-1,viewportHeight,viewportOffset;bool arranging,virtualMode,measureQueued;
 internal Action ViewportChanged;internal string SelectedKey;
 public bool VirtualMode{get{return virtualMode;}}
 internal int ModelCount{get{return model.Count;}}
 internal VirtualLibraryPanel(){DoubleBuffered=true;AutoSize=true;WrapContents=false;FlowDirection=FlowDirection.TopDown;Dock=DockStyle.Top;Margin=Padding.Empty;Padding=Padding.Empty;AccessibleRole=AccessibleRole.List;TabStop=true;}
 internal int MachineHeight(string label,int width){int text=TextRenderer.MeasureText(label,machineFont,new Size(Math.Max(10,width-58),0),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height;return 36+Math.Max(21,text);}
 internal int StandHeight(string text,int width,bool child){int padding=child?46:59;int measured=TextRenderer.MeasureText(text,standFont,new Size(Math.Max(1,width-padding-1),Int32.MaxValue),TextFormatFlags.WordBreak).Height;return Math.Max(40,measured+18);}
 internal void SetRows(IEnumerable<VirtualLibraryRow> rows){
  if(!virtualMode){foreach(Control control in Controls.Cast<Control>().ToArray())control.Dispose();Controls.Clear();shown.Clear();versions.Clear();}
  virtualMode=true;AutoSize=false;model=rows.ToList();var keys=new HashSet<string>(model.Select(row=>row.Key),StringComparer.Ordinal);foreach(string key in measurements.Keys.Where(key=>!keys.Contains(key)).ToArray())measurements.Remove(key);measuredWidth=-1;PerformLayout();if(Parent!=null&&Parent.Parent!=null)Parent.Parent.PerformLayout();
 }
 internal void DisableVirtual(){
  if(!virtualMode)return;virtualMode=false;foreach(var control in shown.Values.ToArray())control.Dispose();shown.Clear();versions.Clear();measurements.Clear();Controls.Clear();model.Clear();positions=new int[1];measuredWidth=-1;AutoSize=true;
 }
 public int ContentHeight(int width){
  if(measuredWidth!=width){positions=new int[model.Count+1];for(int i=0;i<model.Count;i++){var row=model[i];int available=Math.Max(1,width-row.Margin.Horizontal);Measurement known;int height=measurements.TryGetValue(row.Key,out known)&&known.Version==row.Version&&known.Width==available?known.Height:row.EstimatedHeight;positions[i+1]=checked(positions[i]+Math.Max(1,height)+row.Margin.Vertical);}measuredWidth=width;}
  return positions[model.Count];
 }
 internal void Remeasure(){measurements.Clear();measuredWidth=-1;if(Parent!=null&&Parent.Parent!=null)Parent.Parent.PerformLayout();}
 internal void InvalidateMeasurement(string key){if(!measurements.Remove(key))return;measuredWidth=-1;if(measureQueued||!IsHandleCreated)return;measureQueued=true;BeginInvoke((Action)(()=>{measureQueued=false;if(!IsDisposed&&virtualMode&&Parent!=null&&Parent.Parent!=null)Parent.Parent.PerformLayout();}));}
 int At(int y){int index=Array.BinarySearch(positions,y);if(index<0)index=~index-1;return Math.Max(0,Math.Min(model.Count-1,index));}
 public void SetViewport(int width,int height,int offset){
  if(!virtualMode||arranging)return;arranging=true;viewportHeight=Math.Max(1,height);viewportOffset=offset;
  try{
   ContentHeight(width);int start=0,end=0;
   for(int pass=0;pass<3;pass++){
    start=model.Count==0?0:Math.Max(0,At(offset)-1);end=model.Count==0?0:Math.Min(model.Count,At(offset+height)+2);int anchor=model.Count==0?0:At(offset),inside=model.Count==0?0:offset-positions[anchor];bool changed=false;
    for(int i=start;i<end;i++){var row=model[i];int available=Math.Max(1,width-row.Margin.Horizontal);Measurement known;if(!measurements.TryGetValue(row.Key,out known)||known.Version!=row.Version||known.Width!=available){int actual=Math.Max(1,row.Measure(available));measurements[row.Key]=new Measurement{Version=row.Version,Width=available,Height=actual};changed|=actual+row.Margin.Vertical!=positions[i+1]-positions[i];}}
    if(!changed)break;measuredWidth=-1;ContentHeight(width);if(model.Count>0)offset=Math.Max(0,positions[anchor]+inside);
   }
   viewportOffset=offset;var scrolling=Parent==null?null:Parent.Parent as ScrollSurface;if(scrolling!=null){scrolling.SetVirtualRange(positions[model.Count],offset);offset=viewportOffset=scrolling.ScrollOffset;}
   start=model.Count==0?0:Math.Max(0,At(offset)-1);end=model.Count==0?0:Math.Min(model.Count,At(offset+height)+2);
   var visible=new HashSet<string>(Enumerable.Range(start,end-start).Select(i=>model[i].Key),StringComparer.Ordinal);
   foreach(string key in shown.Keys.Where(k=>!visible.Contains(k)).ToArray()){var old=shown[key];if(old.ContainsFocus)Focus();shown.Remove(key);versions.Remove(key);old.Dispose();}
   for(int index=start;index<end;index++){
    var row=model[index];Control control;string version;
    if(!shown.TryGetValue(row.Key,out control)||control.IsDisposed||control.Parent!=this||!versions.TryGetValue(row.Key,out version)||version!=row.Version){
     if(control!=null){if(control.ContainsFocus)Focus();control.Dispose();}
     control=row.Create();control.AutoSize=false;control.Dock=DockStyle.None;shown[row.Key]=control;versions[row.Key]=row.Version;Controls.Add(control);
    }
    control.Tag=row.Tag;control.Margin=row.Margin;if(row.Bind!=null)row.Bind(control);
    int rowWidth=Math.Max(1,width-row.Margin.Horizontal),rowHeight=positions[index+1]-positions[index]-row.Margin.Vertical;
    control.MaximumSize=Size.Empty;control.MinimumSize=new Size(Math.Min(rowWidth,control.MinimumSize.Width),0);
    control.SetBounds(row.Margin.Left,positions[index]-offset+row.Margin.Top,rowWidth,rowHeight);control.TabIndex=index-start;if(Controls.GetChildIndex(control)!=index-start)Controls.SetChildIndex(control,index-start);control.Visible=true;
   }
   if(ViewportChanged!=null)ViewportChanged();
  }finally{arranging=false;}
  Invalidate();
 }
 protected override void OnLayout(LayoutEventArgs e){if(!virtualMode){base.OnLayout(e);return;}if(!arranging)SetViewport(Math.Max(1,Width),Math.Max(1,Height),viewportOffset);}
 internal void EnsureKeyVisible(string key,bool activate){
  int index=model.FindIndex(row=>row.Key==key);if(index<0)return;
  // Lazy measurement can change the target's position during a jump. Settle the
  // viewport before transferring keyboard focus to the requested native row.
  for(int pass=0;pass<8;pass++){ContentHeight(Math.Max(1,Width));int offset=viewportOffset;
   if(positions[index]<offset)offset=positions[index];else if(positions[index+1]>offset+viewportHeight)offset=positions[index+1]-viewportHeight;
   var surface=Parent==null?null:Parent.Parent as ScrollSurface;if(surface!=null)surface.ScrollOffset=offset;SetViewport(Width,Height,surface==null?offset:surface.ScrollOffset);
   if(positions[index]>=viewportOffset&&positions[index+1]<=viewportOffset+viewportHeight)break;
  }
  Control control;if(shown.TryGetValue(key,out control))control.Focus();SelectedKey=key;if(activate&&model[index].Activate!=null)model[index].Activate();
 }
 internal void Navigate(string key,int delta){int index=model.FindIndex(row=>row.Key==key);if(index<0)index=0;index=Math.Max(0,Math.Min(model.Count-1,index+delta));if(model.Count>0)EnsureKeyVisible(model[index].Key,true);}
 protected override bool ProcessDialogKey(Keys keyData){
  if(virtualMode){Keys key=keyData&Keys.KeyCode;string current=SelectedKey;
   foreach(var pair in shown)if(pair.Value.ContainsFocus){current=pair.Key;break;}
   if(key==Keys.Up||key==Keys.Down){Navigate(current,key==Keys.Up?-1:1);return true;}
   if(key==Keys.Home||key==Keys.End){if(model.Count>0)EnsureKeyVisible(model[key==Keys.Home?0:model.Count-1].Key,true);return true;}
   if(key==Keys.PageUp||key==Keys.PageDown){Navigate(current,(key==Keys.PageUp?-1:1)*Math.Max(1,viewportHeight/50));return true;}
   var row=model.FirstOrDefault(item=>item.Key==current);
   if(row!=null&&(key==Keys.Left||key==Keys.Right)){
    if(row.Expand!=null){bool expanded=row.Expanded!=null&&row.Expanded();if(key==Keys.Right&&!expanded||key==Keys.Left&&expanded)row.Expand();else if(key==Keys.Right)Navigate(current,1);return true;}
    if(key==Keys.Left&&row.ParentKey!=null){EnsureKeyVisible(row.ParentKey,true);return true;}
   }
  }return base.ProcessDialogKey(keyData);
 }
 protected override AccessibleObject CreateAccessibilityInstance(){return new VirtualAccessible(this);}
 sealed class VirtualAccessible : ControlAccessibleObject {
  readonly VirtualLibraryPanel panel;internal VirtualAccessible(VirtualLibraryPanel panel):base(panel){this.panel=panel;}
  public override int GetChildCount(){return panel.virtualMode?panel.model.Count:base.GetChildCount();}
  public override AccessibleObject GetChild(int index){if(!panel.virtualMode)return base.GetChild(index);return index>=0&&index<panel.model.Count?new RowAccessible(panel,panel.model[index].Key):null;}
 }
 sealed class RowAccessible : AccessibleObject {
  readonly VirtualLibraryPanel panel;readonly string key;internal RowAccessible(VirtualLibraryPanel panel,string key){this.panel=panel;this.key=key;}
  VirtualLibraryRow Row{get{return panel.model.FirstOrDefault(row=>row.Key==key);}}
  public override string Name{get{return Row==null?"":Row.AccessibleName;}set{}}
  public override AccessibleRole Role{get{return Row!=null&&(Row.Expand!=null||Row.ParentKey!=null)?AccessibleRole.OutlineItem:AccessibleRole.ListItem;}}
  public override AccessibleObject Parent{get{return panel.AccessibilityObject;}}
  public override string DefaultAction{get{return "Выбрать";}}
  public override Rectangle Bounds{get{Control control;return panel.shown.TryGetValue(key,out control)?control.RectangleToScreen(control.ClientRectangle):Rectangle.Empty;}}
  public override AccessibleStates State{get{return AccessibleStates.Selectable|AccessibleStates.Focusable|(panel.SelectedKey==key?AccessibleStates.Selected:AccessibleStates.None)|(Bounds.IsEmpty?AccessibleStates.Offscreen:AccessibleStates.None)|(Row!=null&&Row.Expanded!=null?(Row.Expanded()?AccessibleStates.Expanded:AccessibleStates.Collapsed):AccessibleStates.None);}}
  public override void DoDefaultAction(){if(panel.IsDisposed)return;if(panel.InvokeRequired)panel.BeginInvoke((Action)(()=>panel.EnsureKeyVisible(key,true)));else panel.EnsureKeyVisible(key,true);}
 }
 protected override void Dispose(bool disposing){if(disposing){machineFont.Dispose();standFont.Dispose();}base.Dispose(disposing);}
}
