using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
internal static class Program
{
    private static int checks;
    private static void Check(bool yes,string text){checks++;if(!yes)throw new Exception(text);}
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    [STAThread] private static int Main()
    {
        try
        {
            var source=new TextBlock{Text="Rendering variant A",IsEnabled=false};int cancelled=0;
            using(var progress=new ExportProgressWindow.ProgressWindow(null,source,()=>cancelled++))
            {
                var status=Field<TextBlock>(progress,"status");var button=Field<Button>(progress,"cancelButton");
                Check(!progress.IsVisible&&!progress.ShowActivated&&!progress.Topmost,"Hidden tests must not display or activate windows");
                Check(button.IsEnabled,"Disabled primary/source panel disabled the independent Cancel button");
                Check(status.Text==source.Text,"Initial status binding");source.Text="Saving variant A";
                Check(status.Text==source.Text,"Status updates without polling");
                Check(BindingOperations.GetBinding(status,TextBlock.TextProperty)?.Mode==BindingMode.OneWay,"Status binding can modify source");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(cancelled==1&&!button.IsEnabled,"Cancel must be idempotent and disable its button");
                progress.Close();Check(cancelled==1,"Closing after Cancel called it twice");
                Check(BindingOperations.GetBinding(status,TextBlock.TextProperty)==null,"Closed window retains status binding");
            }
            Check(cancelled==1,"Dispose after close repeated cancellation");
            cancelled=0;
            var completed=new ExportProgressWindow.ProgressWindow(null,source,()=>cancelled++);
            var completedStatus=Field<TextBlock>(completed,"status");completed.Dispose();completed.Dispose();
            Check(cancelled==0,"Normal completion cancelled finished work");
            Check(BindingOperations.GetBinding(completedStatus,TextBlock.TextProperty)==null,"Completion retains binding");
            using(var manual=new ExportProgressWindow.ProgressWindow(null,source,()=>cancelled++)){manual.Close();Check(cancelled==1,"User close does not cancel");}
            cancelled=0;var owner=new Window();
            using(var owned=new ExportProgressWindow.ProgressWindow(owner,source,()=>cancelled++))
            {
                owner.Close();Check(cancelled==1,"Owner closure does not cancel");
                Check(Field<bool>(owned,"closed"),"Progress window remained orphaned after owner closure");
            }
            cancelled=0;var laterOwner=new Window();
            using(var completedBeforeOwner=new ExportProgressWindow.ProgressWindow(laterOwner,source,()=>cancelled++)) completedBeforeOwner.Dispose();
            laterOwner.Close();Check(cancelled==0,"Completed progress retained owner handler");
            using(var ended=new ExportProgressWindow.ProgressWindow(null,source,()=>throw new ObjectDisposedException("work")))ended.Close();
            using(var failure=new ExportProgressWindow.ProgressWindow(null,source,()=>throw new InvalidOperationException("test failure")))
            {Field<Button>(failure,"cancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(Field<TextBlock>(failure,"error").Text.Contains("test failure"),"Cancellation callback failure not reported");}
            Check(source.Text=="Saving variant A","Progress changed caller's status");
            Console.WriteLine($"PASS {checks} lifecycle/binding assertions; no windows shown, no C1");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
