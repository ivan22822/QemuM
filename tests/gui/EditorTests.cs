using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class EditorTests {
    static readonly List<string> checks=new List<string>();
    [DllImport("user32.dll")]static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static void Check(bool condition,string name){if(!condition)throw new Exception(name);checks.Add(name);}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        try{
            using(var form=new Form())using(var editor=Theme.Editor()){
                Theme.Form(form,450,250);form.Controls.Add(editor);form.Show();Application.DoEvents();
                string text=String.Join("\r\n",Enumerable.Range(0,2500).Select(index=>index+" · Кириллица, путь с пробелами C:\\VM Logs\\file.txt"));
                editor.Text=text;Application.DoEvents();
                Check(editor.Text==text,"editor preserves Unicode paths and exact line endings");
                Check((GetWindowLong(editor.Input.Handle,-16)&0x300000)==0,"native edit has neither system scrollbar");
                Check(editor.ScrollBar.Visible&&editor.ScrollBar.Maximum>0,"long text exposes the themed scrollbar");
                editor.ScrollBar.Value=100;Application.DoEvents();
                Check(SendMessage(editor.Input.Handle,0xCE,IntPtr.Zero,IntPtr.Zero).ToInt32()>0,"drag position scrolls native text content");
                editor.ScrollBar.Value=0;SendMessage(editor.Input.Handle,0x20A,new IntPtr(-120<<16),IntPtr.Zero);Application.DoEvents();
                Check(editor.ScrollBar.Value>0||SystemInformation.MouseWheelScrollLines==0,"wheel over text scrolls and updates its themed thumb");
                editor.Input.SelectionStart=0;editor.Input.SelectionLength=1;editor.Input.Focus();SendMessage(editor.Input.Handle,0x102,new IntPtr((int)'Z'),new IntPtr(1));
                Check(editor.Input.CanUndo,"native editing retains undo");editor.Input.Undo();Check(editor.Text==text,"undo restores the exact original content");
                form.ClientSize=new Size(780,400);Application.DoEvents();Check(editor.Text==text,"word wrapping and resizing do not rewrite the text");
                editor.SelectionStart=editor.TextLength;editor.ScrollToCaret();Check(editor.ScrollBar.Value>0,"log autoscroll follows the native caret");
                editor.Clear();Application.DoEvents();Check(!editor.ScrollBar.Visible,"empty text has no scroll thumb");
                form.Close();
            }
            using(var form=new Form())using(var list=new ThemedList()){
                Theme.Form(form,400,250);form.Controls.Add(list);for(int index=0;index<80;index++)list.Items.Add("Тема "+index);form.Show();Application.DoEvents();
                Check((GetWindowLong(list.Handle,-16)&0x300000)==0,"topic list has no native scroll chrome");
                SendMessage(list.Handle,0x100,new IntPtr((int)Keys.End),IntPtr.Zero);Check(list.SelectedIndex==79,"topic list supports keyboard navigation to the last row");
                list.Items.Clear();Check(list.SelectedIndex==-1&&list.SelectedItem==null,"filter reset clears list selection safely");form.Close();
            }
            File.WriteAllText(args[0],J.Json().Serialize(J.O("passed",checks.Count,"checks",checks)));return 0;
        }catch(Exception error){File.WriteAllText(args[0],J.Json().Serialize(J.O("passed",checks.Count,"error",error.ToString())));return 1;}
    }
}
