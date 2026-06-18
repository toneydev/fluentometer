using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Fluentometer.Logic.Ui;

namespace Fluentometer.Controls;

/// <summary>
/// A horizontal gauge bar that reveals a fixed, full-width gradient by "wiping" it left→right to
/// the current Value. The fill container is painted full-width with the gradient and revealed via
/// a Composition InsetClip whose RightInset is animated, so the gradient stays fixed in place (its
/// leading-edge colour tracks fullness) rather than being compressed into the filled width. A soft
/// glow band rides the leading edge.
/// </summary>
public sealed partial class GaugeControl : Control
{
    private const string FillClipPartName = "PART_FillClip";
    private const string GlowPartName = "PART_Glow";

    /// <summary>Fixed width (px) of the leading-edge glow bloom; matches PART_Glow's Width in XAML.</summary>
    private const double GlowWidth = 140.0;

    private Visual? _fillClipVisual;
    private InsetClip? _fillClip;
    private Visual? _glowVisual;

    /// <summary>Progress value in the range [0, 1].</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(GaugeControl),
            new PropertyMetadata(0.0, OnValueChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Brush used to paint the full-width fill gradient (revealed by the wipe clip).</summary>
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(GaugeControl),
            new PropertyMetadata(null));

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Brush for the leading-edge glow band. Typically a transparent→accent gradient.</summary>
    public static readonly DependencyProperty GlowProperty =
        DependencyProperty.Register(nameof(Glow), typeof(Brush), typeof(GaugeControl),
            new PropertyMetadata(null));

    public Brush? Glow
    {
        get => (Brush?)GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    /// <summary>
    /// When true the gauge renders in an "estimate" visual state — lower opacity and a stippled
    /// overlay — to distinguish server-truth from a local estimate.
    /// </summary>
    public static readonly DependencyProperty IsEstimateProperty =
        DependencyProperty.Register(nameof(IsEstimate), typeof(bool), typeof(GaugeControl),
            new PropertyMetadata(false, OnIsEstimateChanged));

    public bool IsEstimate
    {
        get => (bool)GetValue(IsEstimateProperty);
        set => SetValue(IsEstimateProperty, value);
    }

    public GaugeControl()
    {
        DefaultStyleKey = typeof(GaugeControl);
        // Re-apply (snap, no animation) when the control is resized so the reveal + glow stay correct.
        SizeChanged += (_, _) => UpdateFill(animate: false);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild(FillClipPartName) is FrameworkElement fillClipHost)
        {
            _fillClipVisual = ElementCompositionPreview.GetElementVisual(fillClipHost);
            _fillClip = _fillClipVisual.Compositor.CreateInsetClip();
            _fillClipVisual.Clip = _fillClip;
        }

        if (GetTemplateChild(GlowPartName) is FrameworkElement glow)
        {
            _glowVisual = ElementCompositionPreview.GetElementVisual(glow);
        }

        UpdateFill(animate: false);
        UpdateEstimateState();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GaugeControl)d).UpdateFill(animate: true);

    private static void OnIsEstimateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GaugeControl)d).UpdateEstimateState();

    /// <summary>
    /// Reveals the fixed full-width gradient up to Value by animating the fill container's
    /// InsetClip.RightInset, and slides the leading-edge glow band to the reveal tip via Offset.X.
    /// </summary>
    private void UpdateFill(bool animate)
    {
        if (_fillClip is null || _fillClipVisual is null) return;

        var width = ActualWidth;
        if (width <= 0) return; // not laid out yet — SizeChanged re-runs this

        var rightInset = (float)GaugeMath.RightInset(Value, width);
        var glowX = (float)GaugeMath.GlowOffsetX(Value, width, GlowWidth);
        var glowOpacity = GaugeMath.Fraction(Value) > 0.0 ? 1f : 0f;

        var compositor = _fillClipVisual.Compositor;

        if (!animate)
        {
            _fillClip.StopAnimation("RightInset");
            _fillClip.RightInset = rightInset;
            if (_glowVisual is not null)
            {
                _glowVisual.StopAnimation("Offset");
                _glowVisual.Offset = new System.Numerics.Vector3(glowX, 0f, 0f);
                _glowVisual.Opacity = glowOpacity;
            }
            return;
        }

        var clipAnim = compositor.CreateSpringScalarAnimation();
        clipAnim.FinalValue = rightInset;
        clipAnim.DampingRatio = 0.8f;
        clipAnim.Period = TimeSpan.FromMilliseconds(50);
        _fillClip.StartAnimation("RightInset", clipAnim);

        if (_glowVisual is not null)
        {
            var glowAnim = compositor.CreateSpringVector3Animation();
            glowAnim.FinalValue = new System.Numerics.Vector3(glowX, 0f, 0f);
            glowAnim.DampingRatio = 0.8f;
            glowAnim.Period = TimeSpan.FromMilliseconds(50);
            _glowVisual.StartAnimation("Offset", glowAnim);
            _glowVisual.Opacity = glowOpacity;
        }
    }

    private void UpdateEstimateState()
        => VisualStateManager.GoToState(this, IsEstimate ? "Estimate" : "ServerTruth", useTransitions: true);
}
