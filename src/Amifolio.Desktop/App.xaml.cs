using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public partial class App : Application
{
    private Window? window;
    public App()
    {
        StartupTiming.Mark("app.ctor.begin");
        using(StartupTiming.Measure("app.initialize-component"))InitializeComponent();
        UnhandledException+=(_,error)=>
        {
            var arguments=Environment.GetCommandLineArgs();var index=Array.IndexOf(arguments,"--smoke-report");
            if(index>=0&&index+1<arguments.Length)
                try{System.IO.File.WriteAllText(arguments[index+1]+".unhandled.txt",error.Exception.ToString());}catch(System.IO.IOException){}
        };
        StartupTiming.Mark("app.ctor.end");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTiming.Mark("app.on-launched.begin");
        window = new MainWindow();
        using(StartupTiming.Measure("app.window-activate"))window.Activate();
        StartupTiming.Mark("app.on-launched.end");
    }
}
