using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexSwitcher.Core;

namespace CodexSwitcher.App;

public sealed class AccountCard : Border
{
    public Slot Slot {get;}
    public bool HasProfile {get;private set;}
    public bool IsActive {get;private set;}
    public string TraySummary => $"5 ч: {QuotaText(limits?.FiveHour)} · неделя: {QuotaText(limits?.Weekly)}"+(limitsError==null?"":" · не обновлено");
    private static string QuotaText(QuotaWindow? quota)=>quota==null?"—":$"{quota.RemainingPercent:0.#}%";
    public TextBlock Badge {get;}=new();
    public TextBlock Email {get;}=new();
    public TextBlock Date {get;}=new();
    public TextBlock Resets {get;}=new();
    public TextBlock Renewal {get;}=new();
    public TextBlock Updated {get;}=new();
    public Button Switch {get;}=new();
    public Button Login {get;}=new();
    public Button Capture {get;}=new();
    public Button EditRenewal {get;}=new();
    public Button Remove {get;}=new();
    private readonly TextBlock five=new(),week=new(),fiveReset=new(),weekReset=new(),plan=new();
    private readonly ProgressBar fiveBar=new(),weekBar=new();
    private readonly StackPanel resetDetails=new();
    private readonly List<(ResetCredit Credit,TextBlock Text,int Number)> resetRows=new();
    private AccountLimits? limits;
    private string? limitsError;

    public AccountCard(Slot slot)
    {
        Slot=slot;BorderThickness=new Thickness(1);CornerRadius=new CornerRadius(12);Padding=new Thickness(20,16,20,16);Margin=new Thickness(0,0,0,10);
        var row=new Grid();Child=row;
        foreach(var weight in new[]{2.6,1.6,1.6,2.4,1.8})row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(weight,GridUnitType.Star)});
        var identity=new StackPanel {Margin=new Thickness(0,0,20,0),VerticalAlignment=VerticalAlignment.Center};row.Children.Add(identity);
        var heading=new StackPanel {Orientation=Orientation.Horizontal};identity.Children.Add(heading);
        heading.Children.Add(new TextBlock {Text=Label(slot),FontSize=19,FontWeight=FontWeights.SemiBold});
        Badge.FontSize=11;Badge.Margin=new Thickness(8,5,0,0);heading.Children.Add(Badge);
        Remove.Style=(Style)FindResource("LinkButton");Remove.Content="\uE74D";Remove.FontFamily=new FontFamily("Segoe MDL2 Assets");Remove.FontSize=14;Remove.Padding=new Thickness(5,3,5,3);Remove.Margin=new Thickness(5,0,0,0);Remove.ToolTip="Удалить пустую карточку";
        System.Windows.Automation.AutomationProperties.SetName(Remove,"Удалить пустую карточку");heading.Children.Add(Remove);
        Email.FontSize=13;Email.TextTrimming=TextTrimming.CharacterEllipsis;Email.Margin=new Thickness(0,7,0,0);identity.Children.Add(Email);
        plan.FontSize=11;plan.Foreground=Resource("Muted");plan.Margin=new Thickness(0,4,0,0);identity.Children.Add(plan);
        var renewalRow=new Grid {Margin=new Thickness(0,10,0,0)};renewalRow.ColumnDefinitions.Add(new ColumnDefinition());renewalRow.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});identity.Children.Add(renewalRow);
        Renewal.FontSize=11;Renewal.Foreground=Resource("Muted");Renewal.VerticalAlignment=VerticalAlignment.Center;Renewal.TextWrapping=TextWrapping.Wrap;renewalRow.Children.Add(Renewal);
        EditRenewal.Content="Изменить";EditRenewal.Style=(Style)FindResource("LinkButton");EditRenewal.FontSize=11;EditRenewal.Margin=new Thickness(8,0,0,0);EditRenewal.ToolTip="Дата продления подписки указывается вручную";Grid.SetColumn(EditRenewal,1);renewalRow.Children.Add(EditRenewal);
        AddQuota(row,1,"5 часов",five,fiveBar,fiveReset);AddQuota(row,2,"Неделя",week,weekBar,weekReset);
        var resetsPanel=new StackPanel {Margin=new Thickness(0,0,18,0),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(resetsPanel,3);row.Children.Add(resetsPanel);
        Resets.FontSize=13;Resets.FontWeight=FontWeights.SemiBold;Resets.Margin=new Thickness(0,0,0,8);resetsPanel.Children.Add(Resets);
        resetsPanel.Children.Add(new ScrollViewer {Content=resetDetails,MaxHeight=78,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        var actions=new StackPanel {VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(actions,4);row.Children.Add(actions);
        Switch.Style=(Style)FindResource("Primary");Switch.MinHeight=34;Switch.Padding=new Thickness(8,7,8,7);actions.Children.Add(Switch);
        var links=new Grid {Margin=new Thickness(0,6,0,0)};links.ColumnDefinitions.Add(new());links.ColumnDefinitions.Add(new());actions.Children.Add(links);
        Login.Style=(Style)FindResource("LinkButton");Capture.Style=(Style)FindResource("LinkButton");Login.FontSize=11;Capture.FontSize=11;Capture.Content="Сохранить";
        Capture.ToolTip="Сохранить текущий вход Codex в эту карточку";links.Children.Add(Login);Grid.SetColumn(Capture,1);links.Children.Add(Capture);
        Updated.FontSize=11;Updated.TextWrapping=TextWrapping.Wrap;Updated.Foreground=Resource("Muted");Updated.HorizontalAlignment=HorizontalAlignment.Center;Updated.Margin=new Thickness(0,5,0,0);actions.Children.Add(Updated);
        foreach(var button in new[]{Switch,Login,Capture,EditRenewal,Remove})button.Tag=slot;
        foreach(var text in new[]{plan,Renewal,Updated})text.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        SetLimits(null);SetProfile(null,false,false);
    }
    public static string Label(Slot slot)=>"Аккаунт "+((int)slot+1);
    private static Brush Resource(string key)=>(Brush)Application.Current.FindResource(key);
    private static void AddQuota(Grid row,int column,string label,TextBlock value,ProgressBar bar,TextBlock reset)
    {
        var panel=new StackPanel {Margin=new Thickness(0,0,22,0),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(panel,column);row.Children.Add(panel);
        var title=Caption(label);title.FontSize=12;panel.Children.Add(title);
        var numberRow=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,7)};panel.Children.Add(numberRow);
        value.FontSize=26;value.FontWeight=FontWeights.SemiBold;numberRow.Children.Add(value);
        var suffix=Caption("осталось");suffix.FontSize=10;suffix.Margin=new Thickness(6,13,0,0);numberRow.Children.Add(suffix);
        bar.Style=(Style)Application.Current.FindResource("QuotaBar");panel.Children.Add(bar);
        reset.FontSize=11;reset.TextWrapping=TextWrapping.Wrap;reset.Foreground=Resource("Muted");reset.Margin=new Thickness(0,7,0,0);panel.Children.Add(reset);
        reset.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
    }
    public void SetProfile(Profile? profile,bool active,bool enabled)
    {
        HasProfile=profile!=null;IsActive=active;
        Remove.Visibility=profile==null?Visibility.Visible:Visibility.Collapsed;Remove.IsEnabled=profile==null&&enabled;
        Email.Text=profile==null?"Добавьте аккаунт":AuthDocument.Parse(profile.AuthJson).Identity.Email;
        Badge.Text=active?"В Codex":"";Badge.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
        SetResourceReference(BorderBrushProperty,active?"Accent":"Line");
        SetResourceReference(BackgroundProperty,active?"ActiveSurface":"Surface");
        Date.Text=profile==null?"":"Сохранён "+profile.SavedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm",CultureInfo.CurrentCulture);Email.ToolTip=Email.Text+(profile==null?"":"\n"+Date.Text);
        Login.Content="Войти";Login.ToolTip=profile==null?"Добавить аккаунт через браузер":"Повторить вход через браузер";
        Switch.Content=active?"Выбран":profile==null?"Добавить аккаунт":"Переключить";Switch.IsEnabled=!active&&enabled;
        Login.IsEnabled=enabled;EditRenewal.IsEnabled=profile!=null&&enabled;
        Renewal.Text=profile?.RenewalDate is {} date?"Продление "+date.ToString("dd.MM.yyyy")+" · вручную":"Продление не указано";
        Renewal.ToolTip="Дата продления подписки указана вручную, а не получена от Codex";
    }
    public void SetLimits(AccountLimits? value,string? error=null)
    {limits=value;limitsError=error;BuildResetRows();RenderLimits();}
    public void SetLimitsError(string error) {limitsError=error;RenderLimits();}
    private void BuildResetRows()
    {
        resetRows.Clear();resetDetails.Children.Clear();
        if(limits?.ResetCredits is {Count:>0} credits)
        {
            for(var index=0;index<credits.Count;index++)
            {
                var text=new TextBlock {FontSize=11,Margin=new Thickness(0,0,0,4),TextWrapping=TextWrapping.Wrap};resetDetails.Children.Add(text);resetRows.Add((credits[index],text,index+1));
            }
            if(limits.AvailableResets is {} total && total>credits.Count)resetDetails.Children.Add(Caption("Сроки недоступны: "+(total-credits.Count)));
        }
        else resetDetails.Children.Add(Caption(limits?.AvailableResets==0?"Нет доступных сбросов":limits?.AvailableResets is >0?"Сроки не предоставлены":"Нет данных"));
    }
    private static TextBlock Caption(string text)
    {var caption=new TextBlock {Text=text,FontSize=11,TextWrapping=TextWrapping.Wrap};caption.SetResourceReference(TextBlock.ForegroundProperty,"Muted");return caption;}
    public void RenderLimits()
    {
        RenderQuota(limits?.FiveHour,five,fiveBar,fiveReset);RenderQuota(limits?.Weekly,week,weekBar,weekReset);
        Resets.Text="Сбросы · "+(limits?.AvailableResets?.ToString(CultureInfo.CurrentCulture)??"—");plan.Text=PlanName(limits?.Plan);
        Updated.Text=limits==null?limitsError??"Лимиты после входа":$"Обновлено {limits.FetchedAt.ToLocalTime():HH:mm:ss}"+(limitsError==null?"":"\nНе удалось обновить");Updated.ToolTip=limitsError;
        foreach(var (credit,text,number) in resetRows)
        {
            var expiry=credit.ExpiresAt;
            text.Text=expiry is {} at?$"{number} · до {at.ToLocalTime():dd.MM.yyyy HH:mm}":$"{number} · "+(credit.HasExpirationInfo?"без срока истечения":"срок не предоставлен");
            text.SetResourceReference(TextBlock.ForegroundProperty,expiry is {} date && date-DateTimeOffset.UtcNow<TimeSpan.FromDays(3)?"Warning":"Muted");
            text.ToolTip=(credit.Title??"Сброс лимитов")+(credit.GrantedAt is {} granted?$"\nПолучен {granted.ToLocalTime():dd.MM.yyyy HH:mm}":"")+(expiry is {} deadline?$"\n{(deadline<=DateTimeOffset.UtcNow?"Срок истёк":"Истекает "+Countdown(deadline))}":"");
        }
    }
    private static string PlanName(string? value)=>value switch {"prolite"=>"Pro Lite","plus"=>"Plus","pro"=>"Pro","promax"=>"Pro Max","free"=>"Free","go"=>"Go","business"=>"Business","team"=>"Team",null=>"",_=>value};
    private static string Countdown(DateTimeOffset at)
    {
        var delta=at-DateTimeOffset.UtcNow;
        return delta<=TimeSpan.Zero?"ожидается обновление":delta.TotalDays>=1?$"через {(int)delta.TotalDays} д {delta.Hours} ч":$"через {(int)delta.TotalHours} ч {delta.Minutes} мин";
    }
    private static void RenderQuota(QuotaWindow? quota,TextBlock text,ProgressBar bar,TextBlock reset)
    {
        text.Text=quota==null?"—":$"{quota.RemainingPercent:0.#}%";bar.Value=quota?.RemainingPercent??0;bar.Opacity=quota==null ? .25 : 1;
        var color=quota==null?"Muted":quota.RemainingPercent<=10?"Danger":quota.RemainingPercent<=25?"Warning":"Accent";text.SetResourceReference(TextBlock.ForegroundProperty,color);bar.SetResourceReference(ProgressBar.ForegroundProperty,color);
        reset.Text=quota==null?"Нет данных":quota.ResetsAt is {} at?$"{at.ToLocalTime():dd.MM HH:mm} · {Countdown(at)}":"Время обновления неизвестно";
    }
}
