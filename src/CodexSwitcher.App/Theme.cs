using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CodexSwitcher.Core;
using Forms=System.Windows.Forms;

namespace CodexSwitcher.App;

internal static class Theme
{
    internal static bool IsDark {get;private set;}
    internal static bool Load(string path)
    {
        try {SecureFiles.RejectLinks(path);return File.Exists(path)&&new FileInfo(path).Length<=16&&File.ReadAllText(path).Trim()=="dark";}
        catch {return false;}
    }
    internal static void Save(string path)=>SecureFiles.AtomicText(path,IsDark?"dark":"light");
    internal static void Apply(bool dark)
    {
        IsDark=dark;
        var palette=new Dictionary<string,(string Light,string Dark)>
        {
            ["Canvas"]=("#F2F5F2","#141A17"),["Surface"]=("#FFFFFF","#202923"),
            ["ActiveSurface"]=("#F7FCF9","#21392F"),["Ink"]=("#1D2922","#E8F0EA"),
            ["Muted"]=("#65736A","#A8B9AE"),["Line"]=("#DDE5DE","#3A4A40"),
            ["Accent"]=("#167A5D","#69D5AE"),["AccentText"]=("#FFFFFF","#10261D"),
            ["Warning"]=("#996416","#EABF6F"),["Danger"]=("#A93E3E","#FA9696"),
            ["Track"]=("#E7EEE8","#36483D"),["RecoverySurface"]=("#FFF4DE","#3A3021"),
            ["RecoveryLine"]=("#DDB66B","#AC8546")
        };
        foreach(var (key,value) in palette)
            Application.Current.Resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?value.Dark:value.Light));
    }
    internal static System.Drawing.Color DrawingColor(string key)
    {
        var color=((SolidColorBrush)Application.Current.Resources[key]).Color;
        return System.Drawing.Color.FromArgb(color.R,color.G,color.B);
    }
    internal static void ApplyTitleBar(Window window)
    {
        var handle=new WindowInteropHelper(window).Handle;if(handle==IntPtr.Zero)return;
        var dark=IsDark?1:0;DwmSetWindowAttribute(handle,20,ref dark,sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
}

internal sealed class ThemeMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    internal ThemeMenuRenderer():base(new MenuColors()) {RoundedEdges=false;}
    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor=Theme.DrawingColor(!e.Item.Enabled||e.Item is Forms.ToolStripLabel?"Muted":"Ink");
        Forms.TextRenderer.DrawText(e.Graphics,e.Text,e.TextFont,e.TextRectangle,e.TextColor,e.TextFormat);
    }
    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        var r=e.ImageRectangle;
        using var pen=new System.Drawing.Pen(Theme.DrawingColor("Accent"),2);
        e.Graphics.DrawLines(pen,new[]{new System.Drawing.PointF(r.Left+r.Width*.2f,r.Top+r.Height*.5f),new System.Drawing.PointF(r.Left+r.Width*.4f,r.Top+r.Height*.7f),new System.Drawing.PointF(r.Left+r.Width*.8f,r.Top+r.Height*.25f)});
    }
    private sealed class MenuColors : Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground=>Theme.DrawingColor("Surface");
        public override System.Drawing.Color MenuBorder=>Theme.DrawingColor("Line");
        public override System.Drawing.Color MenuItemBorder=>Theme.DrawingColor("Accent");
        public override System.Drawing.Color MenuItemSelected=>Theme.DrawingColor("ActiveSurface");
        public override System.Drawing.Color ImageMarginGradientBegin=>Theme.DrawingColor("Surface");
        public override System.Drawing.Color ImageMarginGradientMiddle=>Theme.DrawingColor("Surface");
        public override System.Drawing.Color ImageMarginGradientEnd=>Theme.DrawingColor("Surface");
        public override System.Drawing.Color CheckBackground=>Theme.DrawingColor("ActiveSurface");
        public override System.Drawing.Color CheckSelectedBackground=>Theme.DrawingColor("ActiveSurface");
        public override System.Drawing.Color CheckPressedBackground=>Theme.DrawingColor("ActiveSurface");
        public override System.Drawing.Color SeparatorDark=>Theme.DrawingColor("Line");
        public override System.Drawing.Color SeparatorLight=>Theme.DrawingColor("Surface");
    }
}
