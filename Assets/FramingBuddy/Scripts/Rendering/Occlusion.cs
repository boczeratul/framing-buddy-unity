using System.Collections.Generic;
using UnityEngine;

namespace FramingBuddy
{
    /// <summary>
    /// 目標可見度：沿目標底部到頂部取樣，從鏡頭發射射線檢查是否被模型、Google 圖磚或地形擋住
    /// （射線停在目標外緣，避免打到目標自己）；並判斷是否在畫面內、佔畫面高度的比例。
    /// </summary>
    public static class Occlusion
    {
        /// <summary>依 id 找目標；找不到時選最醒目（高度／距離最大）的一個</summary>
        public static Target Pick(List<Target> targets, string id, Vector3 eye)
        {
            foreach (var t in targets) if (t.id == id) return t;
            Target best = null;
            float score = 0;
            foreach (var t in targets)
            {
                var l = Geo.ToLocal(t.at);
                float d = Mathf.Max(50, Mathf.Sqrt((float)((l.x - eye.x) * (l.x - eye.x) + (l.y + eye.z) * (l.y + eye.z))));
                float sc = (t.topY - t.baseY) / d;
                if (sc > score)
                {
                    score = sc;
                    best = t;
                }
            }
            return best;
        }

        public static Vector3 Point(Target t, float k)
        {
            var l = Geo.ToLocal(t.at);
            return new Vector3((float)l.x, Mathf.Lerp(t.baseY, t.topY, k), -(float)l.y);
        }

        public static VisibilityReport Compute(Camera cam, Target target)
        {
            if (target == null) return new VisibilityReport();
            var origin = cam.transform.position;
            bool peak = target.kind == TargetKind.Peak;
            int samples = peak ? 8 : 24;
            float span = Mathf.Max(1, target.topY - target.baseY);
            float from = peak ? 1 - 100 / span : 0;
            int visible = 0;
            int mask = ~(1 << PhotoRig.MapOnlyLayer);
            for (int i = 0; i < samples; i++)
            {
                var p = Point(target, from + (1 - from) * (i + 0.5f) / samples);
                if (peak) p.y += 30;
                var dir = p - origin;
                float dist = dir.magnitude;
                float far = Mathf.Max(1, dist - (peak ? Mathf.Max(1500, dist * 0.08f) : target.radius + 2));
                if (!Physics.Raycast(origin + dir / dist * 0.5f, dir / dist, far, mask, QueryTriggerInteraction.Ignore)) visible++;
            }
            var topW = Point(target, 1);
            var baseW = Point(target, 0);
            var top = cam.WorldToViewportPoint(topW);
            var bottom = cam.WorldToViewportPoint(baseW);
            bool front = top.z > 0;
            bool inFrame = peak
                ? front && top.x >= 0 && top.x <= 1 && top.y >= 0 && top.y <= 1
                : front && top.x >= -0.025f && top.x <= 1.025f && Mathf.Min(top.y, bottom.y) <= 1 && Mathf.Max(top.y, bottom.y) >= 0;
            return new VisibilityReport
            {
                target = target,
                visible = visible / (float)samples,
                frameFraction = front ? Mathf.Abs(top.y - bottom.y) : null,
                inFrame = inFrame,
            };
        }
    }
}
