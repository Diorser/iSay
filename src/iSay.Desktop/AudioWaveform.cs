using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace iSay.Desktop;

/// <summary>固定位置的音量细柱，以真实音量驱动错落起伏，不横向滚动声音历史。</summary>
public sealed class AudioWaveform : FrameworkElement
{
    private const int BarCount = 21;
    private const double SilenceFloor = 0.005;
    private const double SettledFloor = 0.001;
    private const double FrameSeconds = 1d / 60;
    private static readonly Brush DefaultWaveBrush = CreateDefaultBrush();
    private static readonly double[] BarWeights = CreateBarWeights();
    private readonly DispatcherTimer animation;
    private readonly double[] barLevels = new double[BarCount];
    private readonly double[] barTargets = new double[BarCount];
    private double targetLevel, peakSinceFrame, smoothedLevel, accentLevel;
    private double motionSeconds;
    private long tickCount, lastFrameTimestamp;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double),
        typeof(AudioWaveform), new FrameworkPropertyMetadata(0d, OnLevelChanged, CoerceLevel));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool),
        typeof(AudioWaveform), new FrameworkPropertyMetadata(false, OnActiveChanged));
    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(nameof(WaveBrush), typeof(Brush),
        typeof(AudioWaveform), new FrameworkPropertyMetadata(DefaultWaveBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Brush WaveBrush { get => (Brush)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty, value); }

    public AudioWaveform()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        animation = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(FrameSeconds)
        };
        animation.Tick += OnAnimationTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? Math.Min(108, Math.Max(0, availableSize.Width)) : 108,
        double.IsFinite(availableSize.Height) ? Math.Min(24, Math.Max(0, availableSize.Height)) : 24);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        var brush = WaveBrush ?? DefaultWaveBrush;
        var step = width / BarCount;
        var barWidth = Math.Min(1.8, step * 0.48);
        var baseline = Math.Min(2, height);
        var maximum = Math.Max(baseline, height - 2);
        for (var index = 0; index < BarCount; index++)
        {
            // 位置固定，每根柱子独立起落；这是音量动效，不把装饰轮廓冒充真实频谱。
            var barHeight = baseline + (maximum - baseline) * (IsActive ? barLevels[index] : 0);
            var rect = new Rect((index + 0.5) * step - barWidth / 2, (height - barHeight) / 2, barWidth, barHeight);
            drawingContext.DrawRoundedRectangle(brush, null, rect, barWidth / 2, barWidth / 2);
        }
    }

    private static object CoerceLevel(DependencyObject owner, object value)
    {
        var level = (double)value;
        return double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0d;
    }

    private static void OnLevelChanged(DependencyObject owner, DependencyPropertyChangedEventArgs change)
    {
        var waveform = (AudioWaveform)owner;
        var nextLevel = RenderableLevel((double)change.NewValue);
        // 重音来自真实音量的上升沿，保留短促发音，不额外等待下一个识别结果。
        waveform.accentLevel = Math.Max(waveform.accentLevel,
            Math.Clamp((nextLevel - waveform.targetLevel) * 2.8, 0, 1));
        waveform.targetLevel = nextLevel;
        waveform.peakSinceFrame = Math.Max(waveform.peakSinceFrame, waveform.targetLevel);
        waveform.UpdateAnimation();
    }

    private static void OnActiveChanged(DependencyObject owner, DependencyPropertyChangedEventArgs change)
    {
        var waveform = (AudioWaveform)owner;
        if (!(bool)change.NewValue) waveform.ResetEnvelope();
        else
        {
            waveform.targetLevel = waveform.peakSinceFrame = RenderableLevel(waveform.Level);
            waveform.accentLevel = 0;
        }
        waveform.InvalidateVisual();
        waveform.UpdateAnimation();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        targetLevel = peakSinceFrame = RenderableLevel(Level);
        accentLevel = 0;
        UpdateAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        animation.Stop();
        ResetEnvelope();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        // 重新出现时只从当前音量起步，不重播隐藏期间已经过去的声音峰值。
        if (!IsVisible) ResetEnvelope();
        else
        {
            targetLevel = peakSinceFrame = RenderableLevel(Level);
            accentLevel = 0;
        }
        InvalidateVisual();
        UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (IsActive && IsLoaded && IsVisible && NeedsAnimation())
        {
            if (!animation.IsEnabled) lastFrameTimestamp = Stopwatch.GetTimestamp();
            animation.Start();
        }
        else animation.Stop();
    }

    private bool NeedsAnimation()
    {
        if (targetLevel > 0 || peakSinceFrame > 0 || smoothedLevel > SettledFloor) return true;
        foreach (var level in barLevels) if (level > SettledFloor) return true;
        return false;
    }

    private void OnAnimationTick(object? sender, EventArgs args)
    {
        if (!IsActive || !IsLoaded || !IsVisible) { animation.Stop(); return; }
        var now = Stopwatch.GetTimestamp();
        var elapsed = lastFrameTimestamp == 0 ? FrameSeconds
            : (now - lastFrameTimestamp) / (double)Stopwatch.Frequency;
        lastFrameTimestamp = now;
        AdvanceEnvelopeFrame(elapsed);
        if (!NeedsAnimation()) animation.Stop();
    }

    private void AdvanceEnvelope() => AdvanceEnvelopeFrame(FrameSeconds);

    private void AdvanceEnvelopeFrame(double elapsedSeconds)
    {
        // 按真实时间计算阻尼，界面帧率波动时也不会把动效变成慢动作。
        var elapsed = double.IsFinite(elapsedSeconds) ? Math.Clamp(elapsedSeconds, 0.001, 0.12) : FrameSeconds;
        tickCount++;
        motionSeconds += elapsed;
        var desired = Math.Max(targetLevel, peakSinceFrame);
        peakSinceFrame = 0;
        // 快速跟随音节，回落保留很短的余韵，避免两层长尾把连续重音抹平。
        smoothedLevel = Follow(smoothedLevel, desired, elapsed, 0.012, 0.047);
        if (Math.Abs(targetLevel - smoothedLevel) <= SettledFloor) smoothedLevel = targetLevel;
        var amplitude = Math.Min(1, Math.Pow(smoothedLevel, 0.66) * 1.28);
        var highestTarget = 0d;
        for (var index = 0; index < BarCount; index++)
        {
            // 每根柱子有不同的高低相位，拉开短柱与高柱，避免三根一组形成均匀的矮墙。
            var phase = index * index * 0.67 + index * 1.47;
            var pulse = 0.5 + 0.5 * Math.Sin(motionSeconds * (16.5 + index % 5 * 0.8) + phase);
            var detail = 0.5 + 0.5 * Math.Sin(motionSeconds * (27 + index % 3 * 1.7) + phase * 1.61);
            var variation = 0.08 + 0.82 * Math.Pow(pulse, 1.7) + 0.10 * detail;
            var accent = 0.22 * accentLevel * (0.2 + 0.8 * pulse);
            barTargets[index] = BarWeights[index] * Math.Min(1, variation + accent);
            highestTarget = Math.Max(highestTarget, barTargets[index]);
        }
        for (var index = 0; index < BarCount; index++)
        {
            // 真实音量控制最高起伏，柱间轮廓只决定相对高低，不因收细和减少柱子压低轻声。
            var target = amplitude * barTargets[index] / highestTarget;
            barLevels[index] = Follow(barLevels[index], target, elapsed,
                0.009 + index % 3 * 0.002, 0.026 + index % 3 * 0.004);
            if (target == 0 && barLevels[index] <= SettledFloor) barLevels[index] = 0;
        }
        accentLevel *= Math.Exp(-elapsed / 0.075);
        if (accentLevel < SettledFloor) accentLevel = 0;
        InvalidateVisual();
    }

    private static double Follow(double current, double target, double elapsed, double attack, double release)
        => current + (target - current) * (1 - Math.Exp(-elapsed / (target > current ? attack : release)));

    private void ResetEnvelope()
    {
        animation.Stop();
        targetLevel = peakSinceFrame = smoothedLevel = accentLevel = 0;
        motionSeconds = 0;
        lastFrameTimestamp = 0;
        Array.Clear(barLevels);
        Array.Clear(barTargets);
    }

    private static double[] CreateBarWeights()
    {
        var weights = new double[BarCount];
        for (var index = 0; index < BarCount; index++)
        {
            var distance = Math.Abs(index - (BarCount - 1) / 2d) / ((BarCount - 1) / 2d);
            // 两端仅轻微收束，叠加不对称轮廓，避免始终呈现中间高、两边低的规整拱形。
            var edge = 0.62 + 0.38 * (1 - Math.Pow(distance, 1.7));
            var irregularity = 0.82 + 0.12 * Math.Sin(index * 1.71 + 0.4)
                + 0.06 * Math.Cos(index * 0.63 + 1.2);
            weights[index] = edge * irregularity;
        }
        // 调整轮廓只改变各柱相对高度，保留最高点的响应，不额外压低轻声。
        var maximum = weights.Max();
        for (var index = 0; index < BarCount; index++) weights[index] /= maximum;
        return weights;
    }

    private static double RenderableLevel(double level) => level <= SilenceFloor ? 0 : (level - SilenceFloor) / (1 - SilenceFloor);
    private static Brush CreateDefaultBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x28, 0xE3, 0xB8));
        brush.Freeze();
        return brush;
    }
}
