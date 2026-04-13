using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Custom UI Graphic that draws a 2D stick figure silhouette using Unity's mesh API.
/// Attach to a full-screen RectTransform on a Canvas.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class StickFigureOverlay : MaskableGraphic
{
    [Header("Figure scale (0-1 relative to rect height)")]
    [Range(0.1f, 0.95f)]
    public float figureHeightRatio = 0.60f;

    [Header("Line thickness in pixels")]
    public float lineThickness = 18f;

    // All positions are defined in a local coordinate system:
    //   origin = center of rect, y-up, x-right
    //   unit = figureHeight (computed each rebuild)

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        Rect r = rectTransform.rect;
        float figH = r.height * figureHeightRatio;

        // ── Key points (fractions of figureHeight, y measured from bottom of figure) ──
        // Figure bottom = pelvis, figure top = top of head
        float cx = 0f;                          // horizontal centre
        float bottom = -figH * 0.5f;            // pelvis Y
        float top    =  figH * 0.5f;            // top of head Y

        // Body proportions (classic stick figure)
        float headRadius   = figH * 0.09f;
        float neckY        = top - headRadius * 2f;
        float shoulderY    = neckY - figH * 0.06f;
        float elbowY       = shoulderY - figH * 0.18f;
        float wristY       = elbowY   - figH * 0.17f;
        float hipY         = shoulderY - figH * 0.28f;
        float shoulderSpan = figH * 0.22f;      // half-width of shoulders
        float hipSpan      = figH * 0.14f;      // half-width of hips

        // Elbow bends outward (arms bent pose like screenshot)
        float elbowOutX    = shoulderSpan + figH * 0.14f;
        // Wrists come back inward / upward
        float wristX       = shoulderSpan - figH * 0.06f;

        Color c = color;

        // ── HEAD (circle approximated as 16-sided polygon) ──
        Vector2 headCenter = new Vector2(cx, neckY + headRadius);
        DrawCircle(vh, headCenter, headRadius, c);

        // ── SPINE (neck → hip centre) ──
        DrawLine(vh, new Vector2(cx, neckY), new Vector2(cx, hipY), lineThickness, c);

        // ── SHOULDERS (left & right) ──
        DrawLine(vh, new Vector2(-shoulderSpan, shoulderY), new Vector2(shoulderSpan, shoulderY), lineThickness, c);

        // ── LEFT ARM: shoulder → elbow → wrist ──
        DrawLine(vh, new Vector2(-shoulderSpan, shoulderY), new Vector2(-elbowOutX, elbowY), lineThickness, c);
        DrawLine(vh, new Vector2(-elbowOutX, elbowY), new Vector2(-wristX, wristY), lineThickness, c);

        // ── RIGHT ARM: shoulder → elbow → wrist ──
        DrawLine(vh, new Vector2(shoulderSpan, shoulderY), new Vector2(elbowOutX, elbowY), lineThickness, c);
        DrawLine(vh, new Vector2(elbowOutX, elbowY), new Vector2(wristX, wristY), lineThickness, c);

        // ── HIPS ──
        DrawLine(vh, new Vector2(-hipSpan, hipY), new Vector2(hipSpan, hipY), lineThickness, c);
    }

    // ─────────────────────────────────────────────────────────────
    //  Primitives
    // ─────────────────────────────────────────────────────────────

    private void DrawLine(VertexHelper vh, Vector2 a, Vector2 b, float thickness, Color c)
    {
        Vector2 dir = (b - a).normalized;
        Vector2 perp = new Vector2(-dir.y, dir.x) * (thickness * 0.5f);

        int idx = vh.currentVertCount;
        AddVertex(vh, a + perp, c);
        AddVertex(vh, a - perp, c);
        AddVertex(vh, b - perp, c);
        AddVertex(vh, b + perp, c);

        vh.AddTriangle(idx, idx + 1, idx + 2);
        vh.AddTriangle(idx, idx + 2, idx + 3);
    }

    private void DrawCircle(VertexHelper vh, Vector2 center, float radius, Color c)
    {
        const int segments = 20;
        int centerIdx = vh.currentVertCount;
        AddVertex(vh, center, c);

        for (int i = 0; i <= segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            AddVertex(vh, center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, c);
        }
        for (int i = 0; i < segments; i++)
            vh.AddTriangle(centerIdx, centerIdx + i + 1, centerIdx + i + 2);
    }

    private static void AddVertex(VertexHelper vh, Vector2 pos, Color c)
    {
        var vert = UIVertex.simpleVert;
        vert.position = pos;
        vert.color = c;
        vh.AddVert(vert);
    }
}
