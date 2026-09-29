using System.Collections.Generic;
using CesiumForUnity;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FramingBuddy
{
    /// <summary>
    /// 地圖的 3D 模式：繞著拍攝位置旋轉的軌道相機（較低畫質、只在開啟時算繪），並畫出取景相機的視錐。
    /// </summary>
    public class OrbitView : MonoBehaviour, MapPanel.IOrbitView
    {
        Camera _cam;
        HDAdditionalCameraData _hd;
        RenderTexture _rt;
        GameObject _frustum;
        MeshFilter _lines, _plane;
        bool _active;
        float _yaw = 200, _pitch = 28, _dist = 140;
        Vector3 _pivot, _panOffset;
        Material _lineMat, _fillMat;
        CesiumCameraManager _cesiumCams;

        public Texture Texture => _rt;

        public void Init(AppAssets assets)
        {
            var go = new GameObject("Map 3D Camera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _hd = go.AddComponent<HDAdditionalCameraData>();
            _cam.nearClipPlane = 0.5f;
            _cam.farClipPlane = 200000f;
            _cam.fieldOfView = 50;
            _hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
            _hd.customRenderingSettings = true;
            // 地圖用途：關掉昂貴的效果
            foreach (var f in new[]
                     {
                         FrameSettingsField.SSR, FrameSettingsField.SSGI, FrameSettingsField.Volumetrics, FrameSettingsField.VolumetricClouds,
                         FrameSettingsField.DepthOfField, FrameSettingsField.MotionBlur, FrameSettingsField.ContactShadows,
                         FrameSettingsField.LensFlareScreenSpace, FrameSettingsField.Bloom, FrameSettingsField.MotionVectors,
                     })
            {
                _hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)f] = true;
                _hd.renderingPathCustomFrameSettings.SetEnabled(f, false);
            }
            _cam.enabled = false;

            _lineMat = assets != null && assets.gizmo != null ? new Material(assets.gizmo) : null;
            _fillMat = assets != null && assets.gizmo != null ? new Material(assets.gizmo) : null;
            if (_lineMat != null)
            {
                SetGizmoColor(_lineMat, new Color(1f, 0.69f, 0.13f, 1f));
                SetGizmoColor(_fillMat, new Color(1f, 0.69f, 0.13f, 0.18f));
            }
            _frustum = new GameObject("Frustum Gizmo") { layer = PhotoRig.MapOnlyLayer };
            _frustum.transform.SetParent(transform, false);
            var l = new GameObject("lines") { layer = PhotoRig.MapOnlyLayer };
            l.transform.SetParent(_frustum.transform, false);
            _lines = l.AddComponent<MeshFilter>();
            var lr = l.AddComponent<MeshRenderer>();
            lr.sharedMaterial = _lineMat;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var p = new GameObject("plane") { layer = PhotoRig.MapOnlyLayer };
            p.transform.SetParent(_frustum.transform, false);
            _plane = p.AddComponent<MeshFilter>();
            var pr = p.AddComponent<MeshRenderer>();
            pr.sharedMaterial = _fillMat;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _frustum.SetActive(false);
        }

        static void SetGizmoColor(Material m, Color c)
        {
            m.SetColor("_UnlitColor", c);
            m.SetColor("_EmissiveColor", new Color(c.r, c.g, c.b) * 0.8f);
            m.SetFloat("_EmissiveExposureWeight", 0f);
        }

        public bool Active
        {
            get => _active;
            set
            {
                _active = value;
                _cam.enabled = value && _rt != null;
                _frustum.SetActive(value);
                if (_cesiumCams == null) _cesiumCams = FindFirstObjectByType<CesiumCameraManager>();
                if (_cesiumCams != null)
                {
                    // 讓 Cesium 也依地圖相機選擇圖磚
                    if (value && !_cesiumCams.additionalCameras.Contains(_cam)) _cesiumCams.additionalCameras.Add(_cam);
                    if (!value) _cesiumCams.additionalCameras.Remove(_cam);
                }
            }
        }

        public void Resize(int w, int h)
        {
            if (_rt != null && _rt.width == w && _rt.height == h) return;
            var old = _rt;
            _rt = new RenderTexture(new RenderTextureDescriptor(w, h, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.D32_SFloat_S8_UInt)) { name = "Map3D" };
            _rt.Create();
            _cam.targetTexture = _rt;
            _cam.aspect = w / (float)h;
            _cam.enabled = _active;
            if (old != null)
            {
                old.Release();
                Destroy(old);
            }
        }

        public void Orbit(Vector2 d)
        {
            _yaw += d.x * 0.35f;
            _pitch = Mathf.Clamp(_pitch + d.y * 0.25f, 3f, 88f);
        }

        public void Pan(Vector2 d)
        {
            var right = Quaternion.Euler(0, _yaw, 0) * Vector3.right;
            var fwd = Quaternion.Euler(0, _yaw, 0) * Vector3.forward;
            float k = _dist * 0.0018f;
            _panOffset += (-right * d.x + fwd * d.y) * k;
        }

        public void Zoom(float wheel) => _dist = Mathf.Clamp(_dist * Mathf.Pow(1.12f, wheel), 8f, 6000f);

        public bool Pick(Vector2 viewport, out LatLon p)
        {
            p = default;
            var ray = _cam.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0));
            if (!Physics.Raycast(ray, out var hit, 100000f, ~(1 << PhotoRig.MapOnlyLayer), QueryTriggerInteraction.Ignore)) return false;
            p = Geo.ToLatLon(hit.point.x, -hit.point.z);
            return true;
        }

        /// <summary>每幀：跟隨取景相機、更新視錐</summary>
        public void Tick(ShotState s, Camera photo)
        {
            if (!_active || photo == null) return;
            var eye = photo.transform.position;
            // 相機移動時軌道中心跟著平移
            _pivot = eye + _panOffset;
            var rot = Quaternion.Euler(_pitch, _yaw, 0);
            _cam.transform.position = _pivot - rot * Vector3.forward * _dist;
            _cam.transform.rotation = rot;

            _frustum.transform.SetPositionAndRotation(eye, photo.transform.rotation);
            var fov = Lens.FieldOfView(s.focal, Lens.FrameSize(s.aspect, s.portrait));
            float L = Mathf.Clamp(_dist * 0.5f, 20f, 800f);
            float hh = Mathf.Tan(fov.v * 0.5f * Mathf.Deg2Rad) * L, hw = Mathf.Tan(fov.h * 0.5f * Mathf.Deg2Rad) * L;
            var c = new[] { new Vector3(-hw, -hh, L), new Vector3(hw, -hh, L), new Vector3(hw, hh, L), new Vector3(-hw, hh, L) };
            var lm = _lines.sharedMesh ?? (_lines.sharedMesh = new Mesh());
            lm.Clear();
            lm.SetVertices(new List<Vector3> { Vector3.zero, c[0], c[1], c[2], c[3] });
            lm.SetIndices(new[] { 0, 1, 0, 2, 0, 3, 0, 4, 1, 2, 2, 3, 3, 4, 4, 1 }, MeshTopology.Lines, 0);
            lm.RecalculateBounds();
            var pm = _plane.sharedMesh ?? (_plane.sharedMesh = new Mesh());
            pm.Clear();
            pm.SetVertices(new List<Vector3>(c));
            pm.SetTriangles(new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }, 0);
            pm.RecalculateBounds();
        }
    }
}
