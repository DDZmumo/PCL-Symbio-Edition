using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PCL.Core.App.Localization;
using PCL.Core.UI.Controls;

using SvgIconControl = PCL.Core.UI.Controls.SvgIcon.SvgIcon;

namespace PCL;

/// <summary>
///     卡片选择框中的单个选项。
/// </summary>
public class MyMsgCardOption
{
    /// <summary>
    ///     卡片标题。
    /// </summary>
    public string Title = "";

    /// <summary>
    ///     卡片副标题说明。
    /// </summary>
    public string Description = "";

    /// <summary>
    ///     卡片图标资源路径。
    /// </summary>
    public string SvgIcon = "lucide/box";

    /// <summary>
    ///     标题旁的小标签，留空则不显示。
    /// </summary>
    public string Tag = "";
}

public partial class MyMsgCardSelect
{
    private readonly ModMain.MyMsgBoxConverter myConverter;
    private readonly int uuid = ModBase.GetUuid();
    private readonly List<CardVisual> cards = [];

    private int selectedIndex = -1;

    public MyMsgCardSelect(ModMain.MyMsgBoxConverter converter)
    {
        try
        {
            InitializeComponent();
            AppendUniqueNameSuffix(Btn1);
            AppendUniqueNameSuffix(Btn2);
            myConverter = converter;
            LabTitle.Text = converter.Title;
            LabCaption.Text = converter.Caption;
            LabCaption.Visibility = string.IsNullOrEmpty(converter.Caption) ? Visibility.Collapsed : Visibility.Visible;
            ShapeLine.StrokeThickness = ModBase.GetWPFSize(1d);
            ConfigurePrimaryButton(converter.Button1, converter.IsWarn);
            ConfigureSecondaryButton(converter.Button2);
            BuildCards(converter.CardOptions, converter.CardSelectedIndex);
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "卡片选择弹窗初始化失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }

        Loaded += Load;
        Btn1.Click += Btn1_Click;
        Btn2.Click += Btn2_Click;
        LabTitle.MouseLeftButtonDown += Drag;
        PanBorder.MouseLeftButtonDown += Drag;
    }

    private void AppendUniqueNameSuffix(FrameworkElement element)
    {
        element.Name += ModBase.GetUuid();
    }

    private void ConfigurePrimaryButton(string text, bool isWarn)
    {
        Btn1.Text = text;
        if (isWarn)
        {
            Btn1.ColorType = MyButton.ColorState.Red;
            LabTitle.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushRedLight");
        }
    }

    private void ConfigureSecondaryButton(string text)
    {
        Btn2.Text = text;
        Btn2.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildCards(List<MyMsgCardOption> options, int initialIndex)
    {
        cards.Clear();
        PanCards.Children.Clear();
        if (options is not null)
            for (var i = 0; i < options.Count; i++)
            {
                var visual = CreateCard(options[i], i);
                cards.Add(visual);
                PanCards.Children.Add(visual.Card);
            }

        selectedIndex = initialIndex >= 0 && initialIndex < cards.Count ? initialIndex : -1;
        Btn1.IsEnabled = selectedIndex >= 0;
        UpdateCards();
    }

    private CardVisual CreateCard(MyMsgCardOption option, int index)
    {
        var icon = new SvgIconControl
        {
            Icon = option.SvgIcon,
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconTile = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon
        };

        var title = new TextBlock
        {
            Text = option.Title,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");

        // 右侧要给选中圆点留位置，避免长标题压到圆点上
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 22, 0),
            Children = { iconTile, title }
        };
        if (!string.IsNullOrEmpty(option.Tag))
        {
            var tagText = new TextBlock { Text = option.Tag, FontSize = 10.5, FontWeight = FontWeights.Bold };
            tagText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush2");
            var tag = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = tagText
            };
            tag.SetResourceReference(Border.BackgroundProperty, "ColorBrush6");
            titleRow.Children.Add(tag);
        }

        var description = new TextBlock
        {
            Text = option.Description,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3");

        var marker = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleRow, 0);
        Grid.SetRow(description, 1);
        Grid.SetRow(marker, 0);
        content.Children.Add(titleRow);
        content.Children.Add(description);
        content.Children.Add(marker);

        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(14, 13, 14, 13),
            MinHeight = 112,
            Margin = new Thickness(4),
            Child = content,
            Cursor = Cursors.Hand
        };
        card.MouseLeftButtonUp += (_, _) => SelectCard(index);
        card.MouseEnter += (_, _) =>
        {
            if (selectedIndex != index) card.SetResourceReference(Border.BackgroundProperty, "ColorBrush7");
        };
        card.MouseLeave += (_, _) =>
        {
            if (selectedIndex != index) card.SetResourceReference(Border.BackgroundProperty, "ColorBrush8");
        };

        return new CardVisual(card, marker, iconTile, icon);
    }

    private void SelectCard(int index)
    {
        if (selectedIndex == index) return;
        selectedIndex = index;
        Btn1.IsEnabled = true;
        UpdateCards();
    }

    private void UpdateCards()
    {
        for (var i = 0; i < cards.Count; i++)
        {
            var visual = cards[i];
            var isSelected = i == selectedIndex;

            visual.Card.SetResourceReference(Border.BackgroundProperty,
                isSelected ? "ColorBrush7" : "ColorBrush8");
            visual.Card.SetResourceReference(Border.BorderBrushProperty,
                isSelected ? "ColorBrush3" : "ColorBrushGray6");
            visual.IconTile.SetResourceReference(Border.BackgroundProperty,
                isSelected ? "ColorBrush3" : "ColorBrush6");
            visual.Icon.SetResourceReference(SvgIconControl.IconBrushProperty,
                isSelected ? "ColorBrushWhite" : "ColorBrush3");
            visual.Marker.SetResourceReference(Border.BorderBrushProperty,
                isSelected ? "ColorBrush3" : "ColorBrushGray5");
            visual.Marker.SetResourceReference(Border.BackgroundProperty,
                isSelected ? "ColorBrush3" : "ColorBrushTransparent");
            visual.Marker.Child = isSelected
                ? new Border
                {
                    Width = 7,
                    Height = 7,
                    CornerRadius = new CornerRadius(4),
                    Background = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
                : null;
        }
    }

    private void Load(object sender, EventArgs e)
    {
        try
        {
            if (Btn2.IsVisible && Btn1.ColorType != MyButton.ColorState.Red)
                Btn1.ColorType = MyButton.ColorState.Highlight;
            Opacity = 0d;
            ModAnimation.AniStart(
                ModAnimation.AaColor(ModMain.frmMain.PanMsgBackground, BlurBorder.BackgroundProperty,
                    (myConverter.IsWarn
                        ? new ModBase.MyColor(140d, 80d, 0d, 0d)
                        : new ModBase.MyColor(90d, 0d, 0d, 0d)) - ModMain.frmMain.PanMsgBackground.Background, 200),
                "PanMsgBackground Background");
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaOpacity(this, 1d, 120, 60),
                    ModAnimation.AaDouble(i => TransformPos.Y += (double)i,
                        -TransformPos.Y, 300, 60, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
                    ModAnimation.AaDouble(i => TransformRotate.Angle += (double)i,
                        -TransformRotate.Angle, 300, 60,
                        new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak))
                }, "MyMsgBox " + uuid);
            ModBase.Log("[Control] 卡片选择弹窗：" + LabTitle.Text);
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "卡片选择弹窗加载失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }
    }

    private void Close()
    {
        myConverter.WaitFrame.Continue = false;
        ComponentDispatcher.PopModal();
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() =>
            {
                if (!ModMain.WaitingMyMsgBox.Any())
                    ModAnimation.AniStart(ModAnimation.AaColor(ModMain.frmMain.PanMsgBackground,
                        BlurBorder.BackgroundProperty,
                        new ModBase.MyColor(0d, 0d, 0d, 0d) - ModMain.frmMain.PanMsgBackground.Background, 200,
                        ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
            }, 30),
            ModAnimation.AaOpacity(this, -Opacity, 80, 20),
            ModAnimation.AaDouble(i => TransformPos.Y += (double)i, 20d - TransformPos.Y,
                150, 0, new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaDouble(i => TransformRotate.Angle += (double)i,
                6d - TransformRotate.Angle, 150, 0, new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() => ((Grid)Parent).Children.Remove(this), after: true)
        }, "MyMsgBox " + uuid);
    }

    public void Btn1_Click(object sender, MouseButtonEventArgs e)
    {
        if (myConverter.IsExited) return;
        if (selectedIndex < 0) return;
        myConverter.IsExited = true;
        myConverter.Result = selectedIndex;
        Close();
    }

    public void Btn2_Click(object sender, MouseButtonEventArgs e)
    {
        if (myConverter.IsExited) return;
        myConverter.IsExited = true;
        myConverter.Result = null;
        Close();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                if (e.GetPosition(ShapeLine).Y <= 2d)
                    ModMain.frmMain.DragMove();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "拖拽移动失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }
    }

    private sealed class CardVisual(Border card, Border marker, Border iconTile, SvgIconControl icon)
    {
        public readonly Border Card = card;
        public readonly Border Marker = marker;
        public readonly Border IconTile = iconTile;
        public readonly SvgIconControl Icon = icon;
    }
}
