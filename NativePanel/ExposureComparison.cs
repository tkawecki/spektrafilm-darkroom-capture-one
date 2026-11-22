// SPDX-License-Identifier: GPL-3.0-only
// Standalone modeless comparison UI. Callers own source capture, worker
// lifetime, color management, snapshot identity and the explicit Apply action.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ExposureComparison
{
    internal static ComparisonWindow Show(Window? owner,string title,double baseEv,double minEv,double maxEv,
        Func<double,CancellationToken,Task<BitmapSource>> render,Func<double,bool> apply,Func<bool> isCurrent)
    {
        var window=new ComparisonWindow(title,baseEv,minEv,maxEv,render,apply,isCurrent);
        if(owner is {IsVisible:true}){window.Owner=owner;window.WindowStartupLocation=WindowStartupLocation.CenterOwner;}
        window.Show();return window;
    }

    internal sealed class ComparisonWindow:Window
    {
        private readonly ExposureComparisonSession session;
        private readonly Func<double,CancellationToken,Task<BitmapSource>> render;
        private readonly Func<double,bool> apply;
        private readonly Func<bool> isCurrent;
        private readonly ComboBox count=new(){ItemsSource=new[]{3,5},SelectedIndex=1,MinWidth=55};
        private readonly ComboBox step=new(){ItemsSource=new[]{"1/3 EV","1/2 EV","1 EV"},SelectedIndex=2,MinWidth=78};
        private readonly ComboBox layout=new(){ItemsSource=new[]{"Full frames","Test strip"},SelectedIndex=0,MinWidth=105};
        private readonly Button renderButton=Button("Render comparison"),cancelButton=Button("Cancel rendering"),applyButton=Button("Apply selected exposure");
        private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};
        private readonly Grid display=new(){Background=new SolidColorBrush(Color.FromRgb(18,20,24)),ClipToBounds=true};
        private readonly CancellationTokenSource closed=new();
        private readonly DispatcherTimer validity=new(){Interval=TimeSpan.FromMilliseconds(500)};
        private readonly List<ManagedFrame> surfaces=new();
        private bool busy,invalid,isClosed;
        private int selected=-1;

        internal ComparisonWindow(string title,double baseEv,double minEv,double maxEv,
            Func<double,CancellationToken,Task<BitmapSource>> render,Func<double,bool> apply,Func<bool> isCurrent)
        {
            this.render=render;this.apply=apply;this.isCurrent=isCurrent;session=new(baseEv,minEv,maxEv);
            Title=title+" — exposure comparison";Width=1180;Height=740;MinWidth=780;MinHeight=510;
            Background=new SolidColorBrush(Color.FromRgb(31,36,43));Foreground=Brushes.Gainsboro;
            var root=new DockPanel{Margin=new Thickness(14)};Content=root;
            var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
            top.Children.Add(new TextBlock{Text=$"{title} · captured exposure {baseEv.ToString("+0.##;-0.##;0",CultureInfo.InvariantCulture)} EV",FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            top.Children.Add(new TextBlock{Text="Each candidate uses the same captured photo and look. Select a result, then Apply. Closing keeps your current settings.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,9)});
            var controls=new WrapPanel{VerticalAlignment=VerticalAlignment.Center};top.Children.Add(controls);
            Add(controls,"Frames",count);Add(controls,"Step",step);Add(controls,"View",layout);controls.Children.Add(renderButton);controls.Children.Add(cancelButton);
            var footer=new DockPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            var close=Button("Close");close.Click+=(_,_)=>Close();DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);DockPanel.SetDock(applyButton,Dock.Right);footer.Children.Add(applyButton);footer.Children.Add(status);
            root.Children.Add(display);applyButton.IsEnabled=cancelButton.IsEnabled=false;
            renderButton.Click+=async(_,_)=>await RenderAsync();cancelButton.Click+=(_,_)=>{session.Invalidate();status.Text="Cancelling comparison…";};
            count.SelectionChanged+=(_,_)=>ConfigurationChanged();step.SelectionChanged+=(_,_)=>ConfigurationChanged();layout.SelectionChanged+=(_,_)=>Draw();
            applyButton.Click+=(_,_)=>Apply();Loaded+=async(_,_)=>await RenderAsync();
            validity.Tick+=(_,_)=>{if(!invalid&&!Current())Invalidate("The photo or settings changed. Reopen comparison to capture the current look.");};validity.Start();
            Closed+=(_,_)=>{isClosed=true;validity.Stop();session.Dispose();closed.Cancel();ClearDisplay();};
        }
        private static Button Button(string text)=>new(){Content=text,Padding=new Thickness(10,6,10,6),Margin=new Thickness(5),Background=Brushes.Gainsboro,Foreground=Brushes.Black};
        private static void Add(Panel panel,string label,ComboBox control)
        {panel.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,7,0)});control.Foreground=Brushes.Black;control.Background=Brushes.Gainsboro;control.Margin=new Thickness(0,4,14,4);control.Padding=new Thickness(4);panel.Children.Add(control);}
        private bool Current(){try{return !isClosed&&isCurrent();}catch{return false;}}
        internal void Invalidate(string reason)
        {
            if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>Invalidate(reason));return;}
            if(isClosed)return;invalid=true;session.Invalidate();display.Opacity=.4;status.Text=reason;status.Foreground=Brushes.LightSalmon;
            renderButton.IsEnabled=applyButton.IsEnabled=count.IsEnabled=step.IsEnabled=false;
        }
        private void ConfigurationChanged()
        {if(busy||isClosed)return;session.Invalidate();applyButton.IsEnabled=false;display.Opacity=.4;status.Text="Settings changed. Render the new comparison.";}
        private async Task RenderAsync()
        {
            if(busy||invalid||isClosed)return;
            if(!Current()){Invalidate("The photo or settings changed. Reopen comparison.");return;}
            try
            {
                busy=true;renderButton.IsEnabled=count.IsEnabled=step.IsEnabled=applyButton.IsEnabled=false;cancelButton.IsEnabled=true;
                ClearDisplay();status.Foreground=Brushes.Gainsboro;status.Text="Rendering exposure 1…";
                await session.RenderAsync((int)count.SelectedItem,new[]{1d/3,.5,1d}[step.SelectedIndex],render,Current,closed.Token,
                    (done,total)=>{if(!isClosed)status.Text=$"Rendered {done} of {total} exposures…";});
                if(isClosed||invalid)return;selected=session.Results.Length/2;display.Opacity=1;Draw();
                var result=session.Results;
                status.Text=$"{result.Length} previews · {result[0].Image.PixelWidth} × {result[0].Image.PixelHeight} pixels each. Select an exposure, then Apply.";applyButton.IsEnabled=session.Ready;
            }
            catch(OperationCanceledException){if(!isClosed&&!invalid)status.Text="Comparison cancelled. Current settings are unchanged.";}
            catch(Exception error){if(!isClosed&&!invalid){status.Text=error.Message;status.Foreground=Brushes.LightSalmon;}}
            finally{busy=false;if(!isClosed){cancelButton.IsEnabled=false;renderButton.IsEnabled=count.IsEnabled=step.IsEnabled=!invalid;}}
        }
        private void Select(int index){selected=index;Draw();}
        private void Draw()
        {
            if(isClosed)return;ClearDisplay();var frames=session.Results;if(frames.Length==0)return;
            var panels=new UniformGrid{Columns=frames.Length};
            if(layout.SelectedIndex==0)
            {
                display.Children.Add(panels);
                for(int i=0;i<frames.Length;i++)
                {
                    var frame=frames[i];var content=new DockPanel();var caption=Caption(frame.Band);DockPanel.SetDock(caption,Dock.Bottom);content.Children.Add(caption);
                    content.Children.Add(Surface(frame.Image));
                    panels.Children.Add(Tile(content,i));
                }
            }
            else
            {
                var body=new DockPanel();display.Children.Add(body);DockPanel.SetDock(panels,Dock.Bottom);body.Children.Add(panels);
                for(int i=0;i<frames.Length;i++)panels.Children.Add(Tile(Caption(frames[i].Band),i));
                body.Children.Add(Surface(ExposureComparisonModel.Compose(frames.Select(x=>x.Image).ToArray())));
            }
        }
        private ManagedFrame Surface(BitmapSource image){var surface=new ManagedFrame(image){Margin=new Thickness(3)};surfaces.Add(surface);return surface;}
        private void ClearDisplay(){foreach(var surface in surfaces)surface.Dispose();surfaces.Clear();display.Children.Clear();}
        private static TextBlock Caption(ExposureBand band)=>new(){Text=$"{band.Label} EV\nExposure {band.Value.ToString("+0.##;-0.##;0",CultureInfo.InvariantCulture)}",TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,7,4,7),Foreground=Brushes.Gainsboro};
        private Button Tile(UIElement content,int index)
        {
            var tile=new Button{Content=content,Padding=new Thickness(3),Margin=new Thickness(3),HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch,
                Background=new SolidColorBrush(Color.FromRgb(30,34,41)),BorderBrush=index==selected?Brushes.CornflowerBlue:Brushes.DimGray,BorderThickness=new Thickness(index==selected?3:1)};
            tile.Click+=(_,_)=>Select(index);return tile;
        }
        private void Apply()
        {
            if(isClosed||invalid||busy)return;
            try{if(session.TryApply(selected,apply,Current))Close();else Invalidate("The comparison is no longer current. Reopen it before applying an exposure.");}
            catch(Exception error){status.Text=error.Message;status.Foreground=Brushes.LightSalmon;}
        }
    }

    // Match the main Viewer: source pixels stay immutable sRGB; monitor ICC is
    // applied only at Paint, independently for each full-frame/strip surface.
    private sealed class ManagedFrame:FrameworkElement,IDisposable
    {
        private readonly BitmapSource image;
        private readonly DrawingGroup original;
        private readonly MonitorColorManagement colors;
        private bool disposed;
        internal ManagedFrame(BitmapSource frame)
        {
            image=frame;original=PreviewDrawing.Create(frame);
            colors=new MonitorColorManagement(Dispatcher,InvalidateVisual,message=>Debug.WriteLine(message));
            SizeChanged+=(_,_)=>InvalidateVisual();
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);if(disposed||ActualWidth<=0||ActualHeight<=0)return;
            double scale=Math.Min(ActualWidth/image.PixelWidth,ActualHeight/image.PixelHeight);
            var painted=colors.GetDrawing(this,image,original);ToolTip=colors.Warning;
            drawing.PushClip(new RectangleGeometry(new Rect(0,0,ActualWidth,ActualHeight)));
            drawing.PushTransform(new TranslateTransform((ActualWidth-image.PixelWidth*scale)/2,(ActualHeight-image.PixelHeight*scale)/2));
            drawing.PushTransform(new ScaleTransform(scale,scale));drawing.DrawDrawing(painted);drawing.Pop();drawing.Pop();drawing.Pop();
        }
        public void Dispose(){if(disposed)return;disposed=true;colors.Dispose();}
    }
}
