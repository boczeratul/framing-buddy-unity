using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FramingBuddy
{
    /// <summary>
    /// 取景相機：HDRP 實體相機（36×24 mm 依畫面比例裁切、焦段、光圈、對焦距離），
    /// 算繪到與取景框同尺寸的 RenderTexture；動態解析度（STP 上採樣）維持流暢；自動對焦、匯出 PNG。
    /// </summary>
    public class PhotoRig : MonoBehaviour
    {
        public Camera Cam { get; private set; }
        public RenderTexture Texture => _rt;
        public float FocusDistance { get; private set; } = 1000;

        HDAdditionalCameraData _hd;
        RenderTexture _rt;
        int _w = 16, _h = 16;
        float _scale = 100f, _minScale = 67f;
        float _avgFrame = 1f / 60f;
        readonly FrameTiming[] _timings = new FrameTiming[1];
        float _afTimer;
        float _afTarget = 1000;
        bool _capturing;
        public event Action<RenderTexture> TextureChanged;

        public const int MapOnlyLayer = 31;

        public void Init()
        {
            var go = new GameObject("Photo Camera");
            go.transform.SetParent(transform, false);
            go.tag = "MainCamera";
            Cam = go.AddComponent<Camera>();
            _hd = go.AddComponent<HDAdditionalCameraData>();
            Cam.nearClipPlane = 0.1f;
            Cam.farClipPlane = 400000f;
            Cam.cullingMask = ~(1 << MapOnlyLayer);
            Cam.usePhysicalProperties = true;
            Cam.gateFit = Camera.GateFitMode.Fill;
            Cam.iso = 100;
            Cam.shutterSpeed = 1f / 125f;
            Cam.bladeCount = 9;
            Cam.curvature = new Vector2(2f, 11f);
            Cam.barrelClipping = 0.25f;
            Cam.anamorphism = 0;
            _hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
            _hd.TAAQuality = HDAdditionalCameraData.TAAQualityLevel.High;
            _hd.allowDynamicResolution = true;
            _hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            _hd.volumeLayerMask = ~0;
            _hd.dithering = true;
            _hd.stopNaNs = true;
            DynamicResolutionHandler.SetDynamicResScaler(() => _scale, DynamicResScalePolicyType.ReturnsPercentage);
            SetSize(1280, 853);
        }

        public void SetQuality(Quality q)
        {
            _minScale = q switch { Quality.Low => 40f, Quality.Medium => 45f, Quality.High => 50f, _ => 60f };
            PixelFactor = q switch { Quality.Low => 0.6f, Quality.Medium => 0.72f, Quality.High => 0.85f, _ => 1f };
            _hd.TAAQuality = q >= Quality.High ? HDAdditionalCameraData.TAAQualityLevel.High : HDAdditionalCameraData.TAAQualityLevel.Medium;
            _scale = Mathf.Clamp(_scale, _minScale, 100f);
        }

        /// <summary>取景框的像素尺寸改變時重建 RenderTexture</summary>
        public void SetSize(int w, int h)
        {
            w = Mathf.Clamp(w, 16, 8192);
            h = Mathf.Clamp(h, 16, 8192);
            if (_rt != null && w == _w && h == _h) return;
            _w = w;
            _h = h;
            var old = _rt;
            _rt = new RenderTexture(new RenderTextureDescriptor(w, h, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.D32_SFloat_S8_UInt)
            {
                msaaSamples = 1,
                // 動態解析度只作用在 HDRP 內部緩衝，輸出貼圖維持全尺寸
                useDynamicScale = false,
            })
            { name = "Viewfinder" };
            _rt.Create();
            Cam.targetTexture = _rt;
            if (old != null)
            {
                old.Release();
                Destroy(old);
            }
            TextureChanged?.Invoke(_rt);
        }

        /// <summary>依拍攝設定擺放相機</summary>
        public void Apply(ShotState s, Vector3 eye)
        {
            var t = Cam.transform;
            t.position = eye;
            t.rotation = Quaternion.Euler(-s.pitch, s.azimuth, -s.roll);
            var frame = Lens.FrameSize(s.aspect, s.portrait);
            Cam.sensorSize = frame;
            Cam.focalLength = s.focal;
            Cam.aperture = Mathf.Clamp(s.aperture, Camera.kMinAperture, Camera.kMaxAperture);
            if (s.focus > 0) _afTarget = s.focus;
        }

        void LateUpdate()
        {
            if (Cam == null) return;
            // 動態解析度：依 GPU 時間（沒有時用影格時間）維持約 60 fps
            float dt = Time.unscaledDeltaTime;
            float gpu = -1;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
            {
                CpuMs = Mathf.Lerp(CpuMs, (float)_timings[0].cpuFrameTime, 0.05f);
                if (_timings[0].gpuFrameTime > 0)
                {
                    gpu = (float)_timings[0].gpuFrameTime / 1000f;
                    GpuMs = Mathf.Lerp(GpuMs, (float)_timings[0].gpuFrameTime, 0.05f);
                }
            }
            _avgFrame = Mathf.Lerp(_avgFrame, gpu > 0 ? gpu : dt, 0.08f);
            if (!_capturing)
            {
                float hi = gpu > 0 ? 1f / 68f : 1f / 52f, lo = gpu > 0 ? 1f / 90f : 1f / 66f;
                if (_avgFrame > hi) _scale = Mathf.Max(_minScale, _scale - 1.5f);
                else if (_avgFrame < lo) _scale = Mathf.Min(100f, _scale + 0.5f);
            }
            else _scale = 100f;
            Cam.focusDistance = FocusDistance = Mathf.Lerp(FocusDistance, _afTarget, 1f - Mathf.Exp(-dt * 8f));
        }

        /// <summary>自動對焦：畫面中央的射線（每 0.15 秒）</summary>
        public void AutoFocus(ShotState s)
        {
            if (s.focus > 0) return;
            _afTimer -= Time.unscaledDeltaTime;
            if (_afTimer > 0) return;
            _afTimer = 0.15f;
            var t = Cam.transform;
            _afTarget = Physics.Raycast(t.position, t.forward, out var hit, 200000f, ~(1 << MapOnlyLayer), QueryTriggerInteraction.Ignore)
                ? Mathf.Max(0.3f, hit.distance)
                : Lens.Hyperfocal(s.focal, s.aperture) * 2f;
        }

        public float RenderScale => _scale;
        public float CpuMs { get; private set; }
        public float GpuMs { get; private set; }

        /// <summary>取景框貼圖相對螢幕像素的比例（Retina 螢幕上略低於原生解析度可省下大量後製成本）</summary>
        public float PixelFactor { get; private set; } = 0.85f;

        // ---- 匯出 ----

        public void Snapshot(ShotState s, bool highRes, Action<string> done)
        {
            if (_capturing) return;
            StartCoroutine(Capture(s.Clone(), highRes, done));
        }

        IEnumerator Capture(ShotState s, bool highRes, Action<string> done)
        {
            _capturing = true;
            int w0 = _w, h0 = _h;
            if (highRes)
            {
                // 長邊 7680 px（約 8K），讓 TAA 與曝光穩定幾個影格後再讀回
                float k = 7680f / Mathf.Max(_w, _h);
                SetSize(Mathf.RoundToInt(_w * k), Mathf.RoundToInt(_h * k));
                for (int i = 0; i < 24; i++) yield return null;
            }
            else
            {
                for (int i = 0; i < 4; i++) yield return null;
            }
            yield return new WaitForEndOfFrame();
            var req = AsyncGPUReadback.Request(_rt, 0, TextureFormat.RGBA32);
            while (!req.done) yield return null;
            string path = null;
            if (!req.hasError)
            {
                var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(req.GetData<byte>());
                tex.Apply(false);
                var png = tex.EncodeToPNG();
                Destroy(tex);
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Framing Buddy");
                Directory.CreateDirectory(dir);
                string name = $"framing-{s.date}-{TimeUtil.FormatMinutes(s.minutes).Replace(":", "")}-{Mathf.RoundToInt(s.focal)}mm-f{s.aperture:0.#}{(highRes ? "-8k" : "")}.png";
                path = Path.Combine(dir, name);
                File.WriteAllBytes(path, png);
                Debug.Log("[snapshot] " + path);
            }
            if (highRes) SetSize(w0, h0);
            _capturing = false;
            done?.Invoke(path);
        }
    }
}
