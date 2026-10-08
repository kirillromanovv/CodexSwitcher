using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexSwitcher.Core;

namespace CodexSwitcher.App;

public partial class App : Application
{
    private Mutex? mutex;
    private FileStream? lockFile;
    private bool ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Apply(e.Args.Contains("--dark"));
        DispatcherUnhandledException += (_,eventArgs)=>
        {MessageBox.Show("Операция остановлена. Перезапустите переключатель. Если появится восстановление, выполните его перед новым переключением.","Codex Switcher"); eventArgs.Handled=true;};
        if(e.Args.Length>=2&&e.Args[0]=="--theme-check")
        {
            var root=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!,"theme-check-"+Guid.NewGuid().ToString("N"));
            SecureFiles.PrivateDirectory(root);var path=Path.Combine(root,"theme.txt");
            try
            {
                if(Theme.Load(path))throw new InvalidOperationException("Missing preference did not default to light");
                foreach(var dark in new[]{true,false}) {Theme.Apply(dark);Theme.Save(path);if(Theme.Load(path)!=dark)throw new InvalidOperationException("Theme preference did not persist");}
                File.WriteAllText(path,"invalid");if(Theme.Load(path))throw new InvalidOperationException("Invalid preference did not default to light");
                using var window=new MainWindow(true,"saved");window.VerifyThemeSwitch();window.VerifyAccountAddition();
                File.WriteAllText(Path.GetFullPath(e.Args[1]),"{\"ThemePersistence\":true,\"LiveThemeSwitch\":true,\"InvalidPreferenceDefaultsLight\":true}");
            }
            finally {SecureFiles.RejectLinks(root);Directory.Delete(root,true);}
            Shutdown(0);return;
        }
        if(e.Args.Length>=2 && e.Args[0]=="--preview-tray")
        {
            var state=e.Args.Length>=3?e.Args[2]:"saved";
            using var window=new MainWindow(true,state);
            window.RenderTrayPreview(Path.GetFullPath(e.Args[1]),state);Shutdown(0);return;
        }
        if(e.Args.Length>=2 && e.Args[0]=="--preview")
        {
            var state=e.Args.Length>=3?e.Args[2]:"empty";
            var window=new MainWindow(true,state);
            window.VerifyThemeSwitch();
            MainWindow=window;
            var content=(FrameworkElement)window.Content;
            var height=state=="recovery"?1030:940;
            var width=e.Args.Length>=4&&int.TryParse(e.Args[3],out var requested)?Math.Clamp(requested,800,2000):1260;
            content.Measure(new Size(width,height)); content.Arrange(new Rect(0,0,width,height)); content.UpdateLayout();
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.GetFullPath(e.Args[1]))) png.Save(stream);
            window.Dispose(); Shutdown(0);return;
        }
        try
        {
            mutex=new Mutex(false,"Local\\CodexSwitcher-"+System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
            try {ownsMutex=mutex.WaitOne(0);} catch(AbandonedMutexException) {ownsMutex=true;}
            if(!ownsMutex) {MessageBox.Show("Codex Switcher уже запущен. Откройте его через значок в трее.","Codex Switcher");Shutdown();return;}
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Codex Switcher");
            SecureFiles.PrivateDirectory(root);
            lockFile=new FileStream(Path.Combine(root,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            Theme.Apply(Theme.Load(Path.Combine(root,"theme.txt")));
            var main=new MainWindow(false,"empty",root); MainWindow=main;main.Show();
        }
        catch
        {MessageBox.Show("Не удалось открыть защищённое хранилище. Проверьте пользователя Windows и закройте другой экземпляр переключателя.","Codex Switcher");Shutdown(1);}
    }
    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.Dispose(); lockFile?.Dispose();
        if(ownsMutex) mutex?.ReleaseMutex(); mutex?.Dispose();base.OnExit(e);
    }
}
