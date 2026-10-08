using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexSwitcher.Core;
using Forms=System.Windows.Forms;

namespace CodexSwitcher.App;

public partial class MainWindow : Window,IDisposable
{
    private readonly bool preview;
    private readonly Vault? vault;
    private readonly string home,authPath;
    private StorePackage? package;
    private CancellationTokenSource? cancellation;
    private Forms.NotifyIcon? tray;
    private readonly string? themePath;
    private Forms.ToolStripMenuItem? trayTheme;
    private readonly Dictionary<Slot,(Forms.ToolStripMenuItem Account,Forms.ToolStripLabel Limits)> trayAccounts=new();
    private bool busy,exiting;
    private bool ready;
    private readonly Dictionary<Slot,AccountCard> accountCards=new();
    private readonly Dictionary<Slot,string> limitsKeys=new();
    private readonly DispatcherTimer pollTimer=new() {Interval=TimeSpan.FromMinutes(1)};
    private readonly DispatcherTimer clockTimer=new() {Interval=TimeSpan.FromSeconds(1)};
    private Task? limitsTask;
    private CancellationTokenSource? limitsCancellation;
    private DateTimeOffset nextLimitsRefresh;
    public MainWindow(bool preview=false,string state="empty",string? dataRoot=null)
    {
        this.preview=preview;
        themePath=preview?null:Path.Combine(dataRoot!,"theme.txt");
        home=Path.GetFullPath(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex"));
        authPath=Path.Combine(home,"auth.json");
        if(!preview) vault=new Vault(dataRoot!);
        InitializeComponent();
        UpdateThemeIcon();
        SourceInitialized+=(_,_)=>Theme.ApplyTitleBar(this);
        if(!preview) {MinWidth=Math.Min(MinWidth,SystemParameters.WorkArea.Width-40);Width=Math.Min(Width,SystemParameters.WorkArea.Width-40);Height=Math.Min(Height,SystemParameters.WorkArea.Height-40);}
        foreach(var slot in preview?Enumerable.Range(0,state=="saved"?5:2).Select(id=>(Slot)id):vault!.Slots())AddCard(slot);
        if(preview)
        {
            foreach(var card in accountCards.Values) {card.SetProfile(null,false,true);card.Capture.IsEnabled=true;}
            CurrentEmail.Text="account1@example.com";CurrentHint.Text="Сохраните текущий вход в нужную карточку.";
            Status.Text="Лимиты обновляются автоматически. Выберите аккаунт для работы.";
            if(state=="saved")
            {
                foreach(var pair in accountCards)
                {
                    var n=(int)pair.Key+1;var profile=new Profile(pair.Key,PreviewAuth(n),DateTimeOffset.Now,DateOnly.FromDateTime(DateTime.Today.AddDays(12+n)));
                    pair.Value.SetProfile(profile,n==1,true);pair.Value.Capture.IsEnabled=true;
                    var resets=n==2?0:n==4?3:n==5?1:2;
                    var rows=n==2?Array.Empty<ResetCredit>():n==5?new[]{new ResetCredit("preview-soon",DateTimeOffset.Now.AddDays(1),true,DateTimeOffset.Now.AddDays(-10),"Сброс лимитов")}:new[]{new ResetCredit("preview-first",DateTimeOffset.Now.AddDays(3),true,DateTimeOffset.Now.AddDays(-7),"Сброс лимитов"),new ResetCredit("preview-second",DateTimeOffset.Now.AddDays(16),true,DateTimeOffset.Now.AddDays(-2),"Сброс лимитов")};
                    pair.Value.SetLimits(new AccountLimits(new QuotaWindow(n==5?12:95-n*11,DateTimeOffset.Now.AddHours(3)),new QuotaWindow(n==5?7:87-n*12,DateTimeOffset.Now.AddDays(3)),resets,n%2==1?"prolite":"plus",DateTimeOffset.Now,rows));
                }
                CurrentHint.Text="Аккаунт 1 · выбран в Codex";
            }
            if(state=="recovery") {RecoveryPanel.Visibility=Visibility.Visible;Cards.IsEnabled=false;Status.Text="Сначала восстановите предыдущий вход.";}
        }
        else
        {
            CreateTray();
            Loaded+=async (_,_)=>{await Run(async ()=>{await ResolvePackage();RefreshState();},false);pollTimer.Start();clockTimer.Start();};
            Activated+=(_,_)=>{if(ready&&!busy) {RefreshState();StartLimitsRefresh();}};
            pollTimer.Tick+=(_,_)=>StartLimitsRefresh();
            clockTimer.Tick+=(_,_)=>{foreach(var card in accountCards.Values) card.RenderLimits();};
        }
    }
    private void AddCard(Slot slot)
    {
        if(accountCards.ContainsKey(slot))return;
        var card=new AccountCard(slot);var row=Cards.RowDefinitions.Count;accountCards.Add(slot,card);
        Cards.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});Grid.SetRow(card,row);Cards.Children.Add(card);
        card.Switch.Click+=SwitchClick;card.Login.Click+=LoginClick;card.Capture.Click+=CaptureClick;card.EditRenewal.Click+=RenewalClick;card.Remove.Click+=RemoveAccountClick;
    }
    private async void AddAccountClick(object sender,RoutedEventArgs e)
    {
        if(preview) {AddCard((Slot)(accountCards.Count==0?0:accountCards.Keys.Max(slot=>(int)slot)+1));return;}
        await Run(()=>
        {
            var slot=vault!.AddSlot();AddCard(slot);
            var previous=tray!.ContextMenuStrip;trayAccounts.Clear();tray.ContextMenuStrip=CreateTrayMenu();previous?.Dispose();
            Status.Text=AccountCard.Label(slot)+" добавлен. Нажмите «Войти» или сохраните текущий вход.";
            return Task.CompletedTask;
        },false);
    }
    private async void RemoveAccountClick(object sender,RoutedEventArgs e)
    {
        var slot=SlotOf(sender);
        if(preview) {if(!accountCards[slot].HasProfile)RemoveCard(slot);return;}
        await Run(()=>
        {
            vault!.RemoveEmptySlot(slot);RemoveCard(slot);
            var previous=tray!.ContextMenuStrip;trayAccounts.Clear();tray.ContextMenuStrip=CreateTrayMenu();previous?.Dispose();
            Status.Text="Пустая карточка удалена.";return Task.CompletedTask;
        },false);
    }
    private void RemoveCard(Slot slot)
    {
        Cards.Children.Remove(accountCards[slot]);accountCards.Remove(slot);limitsKeys.Remove(slot);
        Cards.RowDefinitions.Clear();
        foreach(var card in accountCards.Values)
        {Grid.SetRow(card,Cards.RowDefinitions.Count);Cards.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});}
    }
    private static string PreviewAuth(int n)
    {
        var payload=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"sub\":\"preview"+n+"\",\"email\":\"account"+n+"@example.com\"}")).TrimEnd('=').Replace('+','-').Replace('/','_');
        return "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"id_token\":\"e30."+payload+".preview\",\"access_token\":\"preview\",\"refresh_token\":\"preview\",\"account_id\":\"preview"+n+"\"}}";
    }
    private async Task ResolvePackage()
    {package=await StorePackage.FindAsync();HomePolicy.Check(home);ready=true;}
    private void RefreshState()
    {
        if(preview || vault==null) return;
        string? currentKey=null; bool hasCurrent=false;
        try
        {
            SecureFiles.RejectLinks(authPath);
            if(File.Exists(authPath)) {var current=AuthDocument.Parse(File.ReadAllText(authPath));currentKey=current.Identity.Key;CurrentEmail.Text=current.Identity.Email;hasCurrent=true;}
            else CurrentEmail.Text="Вход в Codex не найден";
        }
        catch {CurrentEmail.Text="Текущий вход недоступен";}
        CurrentEmail.ToolTip=home;
        CurrentHint.Text=hasCurrent?"Текущий вход прочитан из Codex.":"Войдите в Codex или выберите сохранённый аккаунт.";
        var known=false;
        foreach(var slot in accountCards.Keys.ToArray())
        {
            var card=accountCards[slot];
            try
            {
                var profile=vault.Load(slot);var key=profile==null?null:AuthDocument.Parse(profile.AuthJson).Identity.Key;
                var active=key!=null&&key==currentKey;known|=active;
                card.SetProfile(profile,active,ready&&!busy);card.Capture.IsEnabled=ready&&hasCurrent;
                if(key==null || !limitsKeys.TryGetValue(slot,out var previousKey) || key!=previousKey)
                {card.SetLimits(null);if(key!=null)limitsKeys[slot]=key;else limitsKeys.Remove(slot);}
            }
            catch {card.SetProfile(null,false,ready&&!busy);card.Remove.Visibility=Visibility.Collapsed;card.Email.Text="Вход недоступен";card.Badge.Text="Повторите вход";card.Capture.IsEnabled=ready&&hasCurrent;card.SetLimits(null,"Повторите вход");}
        }
        if(hasCurrent&&!known) CurrentHint.Text="Этот аккаунт ещё не сохранён. Нажмите «Сохранить текущий» в нужной карточке.";
        RecoveryPanel.Visibility=vault.HasRecovery?Visibility.Visible:Visibility.Collapsed;
        Cards.IsEnabled=ready&&!busy&&!vault.HasRecovery;
        RefreshButton.IsEnabled=!busy;
        AddAccountButton.IsEnabled=ready&&!busy&&!vault.HasRecovery;
        RefreshTray();
    }
    private async Task Run(Func<Task> action,bool login)
    {
        if(busy||preview||exiting) return;
        busy=true;cancellation=new CancellationTokenSource();Cards.IsEnabled=false;RefreshButton.IsEnabled=false;AddAccountButton.IsEnabled=false;
        RefreshTray();
        RecoveryPanel.IsEnabled=false;Progress.Visibility=Visibility.Visible;CancelLogin.Visibility=login?Visibility.Visible:Visibility.Collapsed;
        CancelLogin.IsEnabled=true;
        try {await StopLimitsAsync();await action();}
        catch(OperationCanceledException) {Status.Text="Операция отменена. Текущий вход сохранён.";}
        catch(UserFault fault) {Status.Text=fault.Message;}
        catch {Status.Text="Операция не завершена. Проверьте соединение и доступ к папке входа, затем повторите.";}
        finally
        {
            cancellation.Dispose();cancellation=null;busy=false;Progress.Visibility=Visibility.Collapsed;CancelLogin.Visibility=Visibility.Collapsed;
            RecoveryPanel.IsEnabled=true;RefreshState();nextLimitsRefresh=DateTimeOffset.MinValue;StartLimitsRefresh();
        }
    }
    private static Slot SlotOf(object sender) => (Slot)((Button)sender).Tag;
    private AccountGateway Gateway() => new(package!.Cli,Path.Combine(vault!.Root,"staging"));
    private Switcher Engine() => new(vault!,authPath,Gateway(),new StoreAppController(package!));
    private async void CaptureClick(object sender,RoutedEventArgs e)
    {
        var slot=SlotOf(sender);
        await Run(()=>
        {
            HomePolicy.Check(home);SecureFiles.RejectLinks(authPath);
            var auth=File.ReadAllText(authPath);var identity=AuthDocument.Parse(auth).Identity;HomePolicy.Check(home,identity);
            CheckDuplicate(slot,identity.Key);
            Profile? previous=null;try {previous=vault!.Load(slot);} catch(UserFault) { }
            if(previous!=null && AuthDocument.Parse(previous.AuthJson).Identity.Key!=identity.Key && MessageBox.Show(this,
                "Заменить сохранённый вход в этой карточке текущим аккаунтом?", "Сохранить текущий",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)
                return Task.CompletedTask;
            vault!.Save(new Profile(slot,auth,DateTimeOffset.UtcNow,previous!=null&&AuthDocument.Parse(previous.AuthJson).Identity.Key==identity.Key?previous.RenewalDate:null));Status.Text="Текущий вход сохранён.";
            return Task.CompletedTask;
        },false);
    }
    private async void LoginClick(object sender,RoutedEventArgs e)
    {
        var slot=SlotOf(sender);
        await Run(async ()=>
        {
            await ResolvePackage();Profile? previous=null;try {previous=vault!.Load(slot);} catch(UserFault) { }
            Status.Text="Войдите в нужный аккаунт в браузере. Текущий вход Codex останется активным.";
            var auth=await Gateway().LoginAsync(url=>Process.Start(new ProcessStartInfo(url){UseShellExecute=true}),cancellation!.Token);
            var identity=AuthDocument.Parse(auth).Identity;HomePolicy.Check(home,identity);CheckDuplicate(slot,identity.Key);
            if(previous!=null && AuthDocument.Parse(previous.AuthJson).Identity.Key!=identity.Key)
                throw new UserFault("В браузере выбран другой аккаунт. Повторите вход с аккаунтом этой карточки.");
            vault!.Save(new Profile(slot,auth,DateTimeOffset.UtcNow,previous?.RenewalDate));Status.Text="Аккаунт добавлен. Теперь можно переключиться.";
        },true);
    }
    private void CheckDuplicate(Slot slot,string key)
    {
        foreach(var other in vault!.Slots().Where(s=>s!=slot))
        {
            Profile? saved=null;try {saved=vault!.Load(other);} catch(UserFault) { }
            if(saved!=null && AuthDocument.Parse(saved.AuthJson).Identity.Key==key)
                throw new UserFault("Этот аккаунт уже сохранён в другой карточке. Выберите другой аккаунт в браузере.");
        }
    }
    private async void SwitchClick(object sender,RoutedEventArgs e)
    {
        if(busy||preview||exiting||!ready)return;
        var slot=SlotOf(sender);Profile? profile=null;try {profile=vault!.Load(slot);}catch(UserFault) { }
        if(profile==null)LoginClick(sender,e);else await SwitchTo(slot);
    }
    private async Task SwitchTo(Slot slot)
    {
        if(busy||exiting||!ready||vault!.HasRecovery) return;
        if(MessageBox.Show(this,"Codex закроется и откроется с выбранным аккаунтом. Сначала завершите активные задачи. Продолжить?",
            "Переключить аккаунт",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
        await Run(async ()=>
        {
            await ResolvePackage();var target=vault.Load(slot)??throw new UserFault("Сначала добавьте аккаунт.");
            HomePolicy.Check(home,AuthDocument.Parse(target.AuthJson).Identity);
            var result=await Engine().SwitchAsync(slot,text=>Status.Text=text,cancellation!.Token);
            Status.Text=result.LaunchSucceeded?"Аккаунт выбран. Codex открыт.":"Аккаунт выбран, но Codex не открылся. Откройте его через меню «Пуск».";
        },false);
    }
    private async void RecoveryClick(object sender,RoutedEventArgs e)
    {
        if(busy) return;
        if(MessageBox.Show(this,"Codex закроется для восстановления предыдущего входа. Завершите активные задачи. Продолжить?",
            "Восстановление",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        await Run(async ()=>{await ResolvePackage();Status.Text="Восстанавливаю предыдущий вход…";var result=await Engine().RecoverAsync(cancellation!.Token);Status.Text=result.LaunchSucceeded?"Предыдущий вход восстановлен. Codex открыт.":"Предыдущий вход восстановлен. Откройте Codex через меню «Пуск».";},false);
    }
    private void CancelClick(object sender,RoutedEventArgs e) {cancellation?.Cancel();CancelLogin.IsEnabled=false;}
    private async void RefreshClick(object sender,RoutedEventArgs e) => await Run(async ()=>{await ResolvePackage();nextLimitsRefresh=DateTimeOffset.MinValue;Status.Text="Обновляю лимиты сохранённых аккаунтов…";},false);
    private void HelpClick(object sender,RoutedEventArgs e) => MessageBox.Show(this,
        "1. Сохраните текущий вход в одну из карточек.\n\n2. Нажмите «Добавить аккаунт», если нужна ещё карточка, и войдите через браузер.\n\n3. Завершите задачи Codex и нажмите «Переключить».\n\nЛимиты обновляются раз в минуту, в том числе в трее. «Обновить» запрашивает их сразу. Сбросы отображаются только при наличии данных; программа их не расходует.\n\nДату продления укажите вручную по данным подписки — Codex её не возвращает.\n\nДля завершения переключателя выберите «Выход» в его трее.","Как пользоваться");
    private async void RenewalClick(object sender,RoutedEventArgs e)
    {
        if(busy||preview||exiting)return;
        var slot=SlotOf(sender);var profile=vault!.Load(slot);if(profile==null)return;
        var picker=new DatePicker {SelectedDate=profile.RenewalDate?.ToDateTime(TimeOnly.MinValue),Margin=new Thickness(0,12,0,12)};
        var panel=new StackPanel {Margin=new Thickness(20)};
        panel.Children.Add(new TextBlock {Text="Дата указана вручную по данным вашей подписки.",TextWrapping=TextWrapping.Wrap});panel.Children.Add(picker);
        var dialog=new Window {Title=AccountCard.Label(slot)+" · продление",Owner=this,Width=390,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Content=panel};
        dialog.SetResourceReference(BackgroundProperty,"Canvas");dialog.SetResourceReference(ForegroundProperty,"Ink");dialog.SourceInitialized+=(_,_)=>Theme.ApplyTitleBar(dialog);
        var save=new Button {Content="Сохранить",IsDefault=true};panel.Children.Add(save);save.Click+=(_,_)=>{dialog.DialogResult=true;};
        if(dialog.ShowDialog()!=true)return;
        await Run(()=>{var latest=vault.Load(slot)??throw new UserFault("Аккаунт больше не сохранён.");vault.Save(latest with {RenewalDate=picker.SelectedDate is {} date?DateOnly.FromDateTime(date):null});Status.Text="Дата продления сохранена вручную.";return Task.CompletedTask;},false);
    }
    private void StartLimitsRefresh()
    {
        if(preview||!ready||busy||exiting||vault!.HasRecovery||limitsTask is {IsCompleted:false}||DateTimeOffset.UtcNow<nextLimitsRefresh)return;
        nextLimitsRefresh=DateTimeOffset.UtcNow.AddMinutes(1);
        limitsCancellation?.Dispose();limitsCancellation=new CancellationTokenSource();
        limitsTask=RefreshLimitsAsync(limitsCancellation.Token);
    }
    private async Task StopLimitsAsync()
    {
        limitsCancellation?.Cancel();
        if(limitsTask!=null)try {await limitsTask;} catch(OperationCanceledException) { }
    }
    private async Task RefreshLimitsAsync(CancellationToken ct)
    {
        foreach(var slot in accountCards.Keys.ToArray())
        {
            if(ct.IsCancellationRequested)return;
            var card=accountCards[slot];
            try
            {
                var profile=vault!.Load(slot);if(profile==null)continue;
                var key=AuthDocument.Parse(profile.AuthJson).Identity.Key;var auth=profile.AuthJson;
                // Use the app's newest credentials when this saved account is active.
                SecureFiles.RejectLinks(authPath);
                if(File.Exists(authPath)) {var live=File.ReadAllText(authPath);if(AuthDocument.Parse(live).Identity.Key==key) auth=live;}
                var result=await Gateway().ReadLimitsAsync(auth,ct);ct.ThrowIfCancellationRequested();
                var latest=vault.Load(slot);
                if(latest==null||AuthDocument.Parse(latest.AuthJson).Identity.Key!=key)continue;
                // Refresh only the encrypted saved profile. Never rewrite live auth during polling.
                if(latest.AuthJson!=result.AuthJson)vault.Save(latest with {AuthJson=result.AuthJson});
                card.SetLimits(result.Limits);limitsKeys[slot]=key;
            }
            catch(OperationCanceledException) {if(ct.IsCancellationRequested)return;card.SetLimitsError("Обновление не удалось · таймаут");}
            catch {card.SetLimitsError("Не удалось обновить · повторите вход или обновите");}
            finally {RefreshTray();}
        }
    }
    private void CreateTray()
    {
        tray=new Forms.NotifyIcon {Text="Codex Switcher",Visible=true};
        var resource=Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Switcher.ico"));
        using(var stream=resource!.Stream) tray.Icon=new System.Drawing.Icon(stream);
        tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(ShowWindow);
        tray.ContextMenuStrip=CreateTrayMenu();
        RefreshTray();
    }
    private Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu=new Forms.ContextMenuStrip {ShowItemToolTips=true,Renderer=new ThemeMenuRenderer()};
        menu.Items.Add("Открыть",null,(_,_)=>Dispatcher.Invoke(ShowWindow));
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach(var slot in accountCards.Keys.ToArray())
        {
            var item=new Forms.ToolStripMenuItem();
            item.Click+=async (_,_)=>{ShowWindow();await SwitchTo(slot);};
            var summary=new Forms.ToolStripLabel {ForeColor=System.Drawing.Color.FromArgb(101,115,106),Margin=Forms.Padding.Empty,Padding=new Forms.Padding(28,0,12,6),TextAlign=System.Drawing.ContentAlignment.MiddleLeft};
            trayAccounts.Add(slot,(item,summary));menu.Items.Add(item);menu.Items.Add(summary);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Обновить лимиты",null,(_,_)=>RefreshClick(this,new RoutedEventArgs()));
        trayTheme=new Forms.ToolStripMenuItem();trayTheme.Click+=(_,_)=>ThemeClick(this,new RoutedEventArgs());menu.Items.Add(trayTheme);
        menu.Items.Add("Выход",null,(_,_)=>Exit());
        menu.Opening+=(_,_)=>{if(!busy&&!preview)RefreshState();else RefreshTray();};
        return menu;
    }
    private void RefreshTray()
    {
        if(trayTheme!=null)trayTheme.Text=Theme.IsDark?"Светлая тема":"Тёмная тема";
        foreach(var (slot,items) in trayAccounts)
        {
            var card=accountCards[slot];
            items.Account.Text=(AccountCard.Label(slot)+" · "+(card.HasProfile?card.Email.Text:"не добавлен")).Replace("&","&&");
            items.Account.Checked=card.IsActive;
            items.Account.Enabled=card.HasProfile&&!card.IsActive&&!busy&&!exiting&&(preview||ready&&!vault!.HasRecovery);
            items.Account.ToolTipText=card.HasProfile?card.Email.Text+"\n"+card.TraySummary+"\n"+card.Updated.Text:"Добавьте аккаунт в окне программы";
            items.Limits.Text=card.HasProfile?card.TraySummary:"Добавьте аккаунт в окне программы";
            items.Limits.ToolTipText=items.Account.ToolTipText;
        }
        if(tray!=null)
        {
            var active=accountCards.Values.FirstOrDefault(card=>card.IsActive);
            var hint=active==null?"Codex Switcher":active.Email.Text+"\n"+active.TraySummary;
            tray.Text=hint.Length>63?hint[..60]+"…":hint;
        }
    }
    private void ThemeClick(object sender,RoutedEventArgs e)
    {
        Theme.Apply(!Theme.IsDark);UpdateThemeIcon();
        Theme.ApplyTitleBar(this);RefreshTray();tray?.ContextMenuStrip?.Invalidate(true);
        if(themePath!=null)try {Theme.Save(themePath);}catch {Status.Text="Тема применена, но сохранить выбор не удалось.";}
    }
    private void UpdateThemeIcon()
    {
        ThemeButton.Content=Theme.IsDark?"\uE706":"\uE708";
        var label=Theme.IsDark?"Включить светлую тему":"Включить тёмную тему";
        ThemeButton.ToolTip=label;System.Windows.Automation.AutomationProperties.SetName(ThemeButton,label);
    }
    internal void VerifyAccountAddition()
    {
        var before=accountCards.Count;AddAccountClick(this,new RoutedEventArgs());
        if(accountCards.Count!=before+1||Cards.RowDefinitions.Count!=accountCards.Count)throw new InvalidOperationException("Dynamic card addition failed");
        var added=accountCards.Keys.Max(slot=>(int)slot);RemoveAccountClick(accountCards[(Slot)added].Remove,new RoutedEventArgs());
        if(accountCards.Count!=before||Cards.RowDefinitions.Count!=before)throw new InvalidOperationException("Empty card removal failed");
        if(accountCards.Values.Any(card=>card.HasProfile&&card.Remove.Visibility!=Visibility.Collapsed))throw new InvalidOperationException("Saved card exposes deletion");
    }
    internal void VerifyThemeSwitch()
    {
        var initial=Theme.IsDark;
        ThemeClick(this,new RoutedEventArgs());
        var background=((SolidColorBrush)accountCards[Slot.Personal].Background).Color;
        var expected=((SolidColorBrush)Application.Current.Resources["Surface"]).Color;
        if(Theme.IsDark==initial||background!=expected)throw new InvalidOperationException("Live theme switch failed");
        ThemeClick(this,new RoutedEventArgs());
        if(Theme.IsDark!=initial)throw new InvalidOperationException("Theme switch back failed");
    }
    internal void RenderTrayPreview(string output,string state)
    {
        using var menu=CreateTrayMenu();RefreshTray();
        menu.PerformLayout();menu.Size=menu.GetPreferredSize(System.Drawing.Size.Empty);menu.CreateControl();
        using var bitmap=new System.Drawing.Bitmap(menu.Width,menu.Height);
        menu.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,menu.Size));
        bitmap.Save(output,System.Drawing.Imaging.ImageFormat.Png);
        // Exercise the same refresh path without opening real profiles or showing a tray icon.
        var sample=accountCards[Slot.Personal];
        sample.SetLimits(new AccountLimits(new QuotaWindow(0,null),new QuotaWindow(100,null),0,"plus",DateTimeOffset.Now));RefreshTray();
        if(state=="saved"&&trayAccounts[Slot.Personal].Limits.Text!="5 ч: 0% · неделя: 100%")throw new InvalidOperationException("Tray refresh failed");
        sample.SetLimitsError("Synthetic offline status");RefreshTray();
        if(state=="saved"&&trayAccounts[Slot.Personal].Limits.Text?.EndsWith("не обновлено")!=true)throw new InvalidOperationException("Tray stale status failed");
        if(state=="empty"&&trayAccounts.Values.Any(items=>items.Account.Enabled))throw new InvalidOperationException("Empty tray actions enabled");
    }
    private void ShowWindow() {Show();WindowState=WindowState.Normal;Activate();}
    private void ExitClick(object sender,RoutedEventArgs e)=>Exit();
    private async void Exit()
    {
        if(busy) {ShowWindow();Status.Text="Дождитесь завершения операции. Вход в браузере можно отменить.";return;}
        exiting=true;pollTimer.Stop();clockTimer.Stop();await StopLimitsAsync();Close();Application.Current.Shutdown();
    }
    protected override void OnClosing(CancelEventArgs e)
    {if(!preview&&!exiting) {e.Cancel=true;Hide();}base.OnClosing(e);}
    public void Dispose() {pollTimer.Stop();clockTimer.Stop();limitsCancellation?.Cancel();if(tray!=null){tray.Visible=false;tray.Icon?.Dispose();tray.ContextMenuStrip?.Dispose();tray.Dispose();tray=null;}}
}

