using System.Collections;
using System.IO;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using UnityEngine;
using UnityEngine.UI;

public class PoseDetectionManager : MonoBehaviour
{
    [Header("UI")]
    public RawImage cameraDisplay;

    // Landmark indices
    private const int NOSE           = 0;
    private const int LEFT_SHOULDER  = 11;
    private const int RIGHT_SHOULDER = 12;
    private const int LEFT_ELBOW     = 13;
    private const int RIGHT_ELBOW    = 14;
    private const int LEFT_WRIST     = 15;
    private const int RIGHT_WRIST    = 16;
    private const int LEFT_HIP       = 23;
    private const int RIGHT_HIP      = 24;

    // Public landmark properties (normalized 0-1 coords + visibility in z)
    public Vector3 Nose           { get; private set; }
    public Vector3 LeftShoulder   { get; private set; }
    public Vector3 RightShoulder  { get; private set; }
    public Vector3 LeftElbow      { get; private set; }
    public Vector3 RightElbow     { get; private set; }
    public Vector3 LeftWrist      { get; private set; }
    public Vector3 RightWrist     { get; private set; }
    public Vector3 LeftHip        { get; private set; }
    public Vector3 RightHip       { get; private set; }

    // Expose camera texture so other scripts (e.g. CalibrationManager) can display it
    public WebCamTexture CameraTexture => _webcamTexture;
    public bool IsReady => _isReady;

    private WebCamTexture _webcamTexture;
    private PoseLandmarker _poseLandmarker;
    private Texture2D _inputTexture;
    private int _frameCount;
    private bool _isReady;

    private IEnumerator Start()
    {
        // Request camera permission and wait for it
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogError("[PoseDetectionManager] Camera permission denied.");
            yield break;
        }

        // Find the front-facing camera
        WebCamDevice? frontCam = null;
        foreach (var device in WebCamTexture.devices)
        {
            if (device.isFrontFacing)
            {
                frontCam = device;
                break;
            }
        }

        // Fallback to any camera if no front cam found
        if (frontCam == null && WebCamTexture.devices.Length > 0)
        {
            frontCam = WebCamTexture.devices[0];
            Debug.LogWarning("[PoseDetectionManager] No front camera found, using default.");
        }

        if (frontCam == null)
        {
            Debug.LogError("[PoseDetectionManager] No camera found.");
            yield break;
        }

        _webcamTexture = new WebCamTexture(frontCam.Value.name, 640, 480, 30);
        _webcamTexture.Play();

        // Wait until the camera produces real frames
        yield return new WaitUntil(() => _webcamTexture.width > 16);

        if (cameraDisplay != null)
            cameraDisplay.texture = _webcamTexture;

        _inputTexture = new Texture2D(_webcamTexture.width, _webcamTexture.height, TextureFormat.RGBA32, false);

        // Load model bytes from StreamingAssets (works on Android)
        string taskPath = Path.Combine(Application.streamingAssetsPath, "pose_landmarker_lite.task");
        yield return LoadModelAndInit(taskPath);
    }

    private IEnumerator LoadModelAndInit(string taskPath)
    {
        byte[] modelBytes = null;

        // UnityWebRequest handles the jar:// path on Android
        var request = UnityEngine.Networking.UnityWebRequest.Get(taskPath);
        yield return request.SendWebRequest();

        if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[PoseDetectionManager] Failed to load model: {request.error}");
            yield break;
        }

        modelBytes = request.downloadHandler.data;
        Debug.Log($"[PoseDetectionManager] Model loaded — {modelBytes.Length} bytes");

        var baseOptions = new BaseOptions(modelAssetBuffer: modelBytes);
        var options = new PoseLandmarkerOptions(
            baseOptions,
            runningMode: Mediapipe.Tasks.Vision.Core.RunningMode.VIDEO,
            numPoses: 1,
            minPoseDetectionConfidence: 0.5f,
            minPosePresenceConfidence: 0.5f,
            minTrackingConfidence: 0.5f
        );

        _poseLandmarker = PoseLandmarker.CreateFromOptions(options);
        _isReady = true;
        Debug.Log("[PoseDetectionManager] PoseLandmarker ready.");
    }

    private void Update()
    {
        if (!_isReady || _webcamTexture == null || !_webcamTexture.isPlaying)
            return;

        // Copy WebCamTexture pixels into a Texture2D
        _inputTexture.SetPixels32(_webcamTexture.GetPixels32());
        _inputTexture.Apply();

        // Build MediaPipe Image from raw RGBA bytes
        var rawBytes = _inputTexture.GetRawTextureData<byte>();
        using var mpImage = new Mediapipe.Image(
            Mediapipe.ImageFormat.Types.Format.Srgba,
            _inputTexture.width,
            _inputTexture.height,
            _inputTexture.width * 4,
            rawBytes);

        long timestampMs = (long)(Time.realtimeSinceStartup * 1000);

        var result = _poseLandmarker.DetectForVideo(mpImage, timestampMs);

        if (result.poseLandmarks != null && result.poseLandmarks.Count > 0)
        {
            var landmarks = result.poseLandmarks[0].landmarks;
            Nose          = ToVector3(landmarks, NOSE);
            LeftShoulder  = ToVector3(landmarks, LEFT_SHOULDER);
            RightShoulder = ToVector3(landmarks, RIGHT_SHOULDER);
            LeftElbow     = ToVector3(landmarks, LEFT_ELBOW);
            RightElbow    = ToVector3(landmarks, RIGHT_ELBOW);
            LeftWrist     = ToVector3(landmarks, LEFT_WRIST);
            RightWrist    = ToVector3(landmarks, RIGHT_WRIST);
            LeftHip       = ToVector3(landmarks, LEFT_HIP);
            RightHip      = ToVector3(landmarks, RIGHT_HIP);

            _frameCount++;
            if (_frameCount % 60 == 0)
            {
                Debug.Log($"[Pose] Nose={Nose:F3} | L.Shoulder={LeftShoulder:F3} | R.Shoulder={RightShoulder:F3}");
                Debug.Log($"[Pose] L.Elbow={LeftElbow:F3} | R.Elbow={RightElbow:F3}");
                Debug.Log($"[Pose] L.Wrist={LeftWrist:F3} | R.Wrist={RightWrist:F3}");
                Debug.Log($"[Pose] L.Hip={LeftHip:F3} | R.Hip={RightHip:F3}");
            }
        }
    }

    private static Vector3 ToVector3(
        System.Collections.Generic.IList<Mediapipe.Tasks.Components.Containers.NormalizedLandmark> landmarks,
        int index)
    {
        if (index >= landmarks.Count) return Vector3.zero;
        var lm = landmarks[index];
        // x, y are normalized [0,1]; z holds visibility score
        return new Vector3(lm.x, lm.y, lm.visibility ?? 0f);
    }

    private void OnDestroy()
    {
        _poseLandmarker?.Close();
        if (_webcamTexture != null && _webcamTexture.isPlaying)
            _webcamTexture.Stop();
        if (_inputTexture != null)
            Destroy(_inputTexture);
    }
}
