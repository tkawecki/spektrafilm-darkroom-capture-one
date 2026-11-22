using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
    private static int checks;
    private static void Check(bool yes,string label){checks++;if(!yes)throw new Exception(label);}
    private const BindingFlags Instance=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,Instance)!.GetValue(value)!;
    private static void Call(object value,string name,params object[] arguments)=>value.GetType().GetMethod(name,Instance)!.Invoke(value,arguments);
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [STAThread] private static int Main()
    {
        try
        {
            var store=new PaperTextureStore(Path.GetFullPath("artifacts/full-function-20261116/paper-editor-test-reference"));
            var blank=new PaperDecorationEditor.EditorWindow(null,store);Check(blank.Result is null,"Opening editor committed a look");
            Check(blank.TryAccept()&&ReferenceEquals(blank.Result,PaperDecorationSettings.Default),"Null/default editor changed neutral settings");blank.Close();Check(blank.Result is null,"Closing without modal Apply did not cancel");
            var layers=ImmutableArray.Create(new PaperTextureLayer(new string('a',64),PaperBlendMode.SoftLight,.12345678901234567,"Long texture name — original.png"),new PaperTextureLayer(new string('b',64),PaperBlendMode.Screen,.9876543210987654,"Second.png"));
            var settings=new PaperDecorationSettings(true,true,.012345678901234567,layers);string original=PaperDecorationStorage.Serialize(settings);
            var unchanged=new PaperDecorationEditor.EditorWindow(settings,store);Check(unchanged.TryAccept(),"Unchanged editor rejected");
            Check(ReferenceEquals(unchanged.Result,settings)&&PaperDecorationStorage.Serialize(unchanged.Result!)==original,"Untouched percent fields quantized exact serialized values");unchanged.Close();
            var edited=new PaperDecorationEditor.EditorWindow(settings,store);var list=Field<ListBox>(edited,"list");
            Check(list.Items.Count==2,"Initial layer count");list.SelectedIndex=1;Click(Field<Button>(edited,"up"));Check(edited.TryAccept(),"Moved list rejected");
            Check(edited.Result!.Layers[0].AssetId==layers[1].AssetId&&edited.Result.Layers[1].Opacity==layers[0].Opacity,"Move lost order or precision");
            list.SelectedIndex=0;Click(Field<Button>(edited,"remove"));Check(edited.TryAccept()&&edited.Result!.Layers.Length==1,"Remove failed");
            var opacity=(TextBox)list.Items[0].GetType().GetProperty("OpacityField",Instance)!.GetValue(list.Items[0])!;opacity.Text="NaN";Check(!edited.TryAccept(),"Nonfinite opacity accepted");
            opacity.Text="25";Field<TextBox>(edited,"padding").Text="101";Check(!edited.TryAccept(),"Oversize percentage accepted");
            Field<TextBox>(edited,"padding").Text="7";Check(edited.TryAccept()&&edited.Result!.Layers[0].Opacity==.25&&edited.Result.PaddingFraction==.07,"Numeric edit not committed");
            var body=(FrameworkElement)edited.Content;body.Measure(new Size(744,532));body.Arrange(new Rect(0,0,744,532));body.UpdateLayout();Check(list.ActualWidth>500,"Offscreen editor layout failed");
            Check(!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(opacity)),"Opacity lacks accessible name");
            Field<bool>(edited,"closed");Call(edited,"RefreshButtons");
            edited.GetType().GetField("importing",Instance)!.SetValue(edited,true);Call(edited,"RefreshButtons");
            Check(!Field<Button>(edited,"apply").IsEnabled&&!Field<Button>(edited,"reset").IsEnabled&&!Field<CheckBox>(edited,"active").IsEnabled,"Import allows competing edits");
            edited.GetType().GetField("importing",Instance)!.SetValue(edited,false);Call(edited,"RefreshButtons");Click(Field<Button>(edited,"reset"));Check(edited.TryAccept()&&edited.Result!.IsIdentity,"Reset not neutral");
            edited.Close();Check(edited.Result is null,"Cancel/close publishes edits");Check(PaperDecorationStorage.Serialize(settings)==original,"Editor mutated input snapshot");
            Console.WriteLine($"PASS {checks} paper editor assertions; offscreen only, no ShowDialog/C1, exact serialized snapshots preserved");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
