using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace NoteUI;

internal static class AnimationHelper
{
    // ── Fade + slide up (note cards appearing) ───────────────────

    public static void FadeSlideIn(UIElement element, int delayMs = 0, int durationMs = 250)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        visual.Opacity = 0f;
        visual.Offset = new Vector3(0, 12, 0);

        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.InsertKeyFrame(0f, 0f);
        fadeAnim.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        fadeAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
        fadeAnim.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        var slideAnim = compositor.CreateVector3KeyFrameAnimation();
        slideAnim.InsertKeyFrame(0f, new Vector3(0, 12, 0));
        slideAnim.InsertKeyFrame(1f, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        slideAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
        slideAnim.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        visual.StartAnimation("Opacity", fadeAnim);
        visual.StartAnimation("Offset", slideAnim);
    }

    // ── Fade out then collapse ───────────────────────────────────

    public static void FadeOut(UIElement element, int durationMs = 150, Action? onComplete = null)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.InsertKeyFrame(0f, 1f);
        fadeAnim.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
        fadeAnim.Duration = TimeSpan.FromMilliseconds(durationMs);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", fadeAnim);
        batch.End();

        if (onComplete != null)
        {
            batch.Completed += (_, _) =>
            {
                element.DispatcherQueue.TryEnqueue(() => onComplete());
            };
        }
    }

    // ── Fade in (restore) ────────────────────────────────────────

    public static void FadeIn(UIElement element, int durationMs = 200, int delayMs = 0)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        visual.Opacity = 0f;

        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.InsertKeyFrame(0f, 0f);
        fadeAnim.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        fadeAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
        fadeAnim.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        visual.StartAnimation("Opacity", fadeAnim);
    }

    // ── Scale pop (pin toggle, button feedback) ──────────────────

    public static void ScalePop(UIElement element, int durationMs = 200)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        // Set center point for scale
        var fe = element as FrameworkElement;
        var w = fe != null ? (float)fe.ActualWidth / 2f : 0f;
        var h = fe != null ? (float)fe.ActualHeight / 2f : 0f;
        visual.CenterPoint = new Vector3(w, h, 0);

        var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0f, Vector3.One);
        scaleAnim.InsertKeyFrame(0.4f, new Vector3(1.05f, 1.05f, 1f), compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f)));
        scaleAnim.InsertKeyFrame(1f, Vector3.One, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f)));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(durationMs);

        visual.StartAnimation("Scale", scaleAnim);
    }
}
