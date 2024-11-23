using BepInEx;
using BepInEx.Configuration;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GorillaReplay
{
    [BepInPlugin("zaynethedev.gorillareplay", "GorillaReplay", "1.5.0")]
    public class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<Vector2> resolution;
        private ConfigEntry<bool> useThirdPersonCamera;
        private Camera recordingCamera;
        private Texture2D frameTexture;
        private ConcurrentQueue<byte[]> frameQueue = new ConcurrentQueue<byte[]>();
        private Process ffmpegProcess;
        private bool isRecording, stopProcessing;
        private InputAction startRecordingAction;
        private InputAction stopRecordingAction;
        private RenderTexture renderTexture;

        void Awake()
        {
            resolution = Config.Bind("Settings", "Resolution", new Vector2(1280, 720), "Recording resolution.");
            useThirdPersonCamera = Config.Bind("Settings", "UseThirdPersonCamera", false, "Record from third-person camera.");

            var inputActions = new InputActionMap("RecordingControls");
            startRecordingAction = inputActions.AddAction("StartRecording", binding: "<Keyboard>/z");
            stopRecordingAction = inputActions.AddAction("StopRecording", binding: "<Keyboard>/x");

            startRecordingAction.performed += ctx => StartRecording();
            stopRecordingAction.performed += ctx => StopRecording();

            inputActions.Enable();
        }

        void Update()
        {
            if (recordingCamera == null)
                recordingCamera = FindCamera();
        }

        Camera FindCamera()
        {
            string cameraPath = useThirdPersonCamera.Value
                ? "Player Objects/Third Person Camera/Shoulder Camera"
                : "Player Objects/Player VR Controller/GorillaPlayer/TurnParent/Main Camera";

            return GameObject.Find(cameraPath)?.GetComponent<Camera>();
        }

        void StartRecording()
        {
            if (isRecording || recordingCamera == null) return;

            isRecording = true;
            stopProcessing = false;

            renderTexture = new RenderTexture((int)resolution.Value.x, (int)resolution.Value.y, 24, RenderTextureFormat.ARGB32);
            frameTexture = new Texture2D((int)resolution.Value.x, (int)resolution.Value.y, TextureFormat.RGB24, false);

            string ffmpegPath = Path.Combine(Paths.PluginPath, "GorillaReplay", "ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) return;

            string outputFilePath = Path.Combine(Paths.PluginPath, "Renders", $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f rawvideo -pix_fmt rgb24 -s {resolution.Value.x}x{resolution.Value.y} -r 60 -i - -vf \"vflip\" -c:v libx264 -preset veryfast -crf 23 \"{outputFilePath}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                }
            };
            ffmpegProcess.Start();

            new Thread(ProcessFrames).Start();
            InvokeRepeating(nameof(CaptureFrame), 0f, 1f / 60f);
        }

        void StopRecording()
        {
            if (!isRecording) return;

            isRecording = false;
            stopProcessing = true;
            CancelInvoke(nameof(CaptureFrame));
            ffmpegProcess.StandardInput.BaseStream.Close();
            ffmpegProcess.WaitForExit();
            ffmpegProcess.Dispose();

            renderTexture.Release();
            renderTexture = null;
        }

        void CaptureFrame()
        {
            recordingCamera.targetTexture = renderTexture;
            recordingCamera.Render();

            RenderTexture.active = renderTexture;
            frameTexture.ReadPixels(new Rect(0, 0, frameTexture.width, frameTexture.height), 0, 0);
            frameTexture.Apply();

            if (frameQueue.Count < 10)
            {
                frameQueue.Enqueue(frameTexture.GetRawTextureData());
            }

            RenderTexture.active = null;
            recordingCamera.targetTexture = null;
        }

        void ProcessFrames()
        {
            using (Stream stream = ffmpegProcess.StandardInput.BaseStream)
            {
                while (!stopProcessing || !frameQueue.IsEmpty)
                {
                    if (frameQueue.TryDequeue(out byte[] frameData))
                        stream.Write(frameData, 0, frameData.Length);
                    else
                        Thread.Sleep(1);
                }
            }
        }
    }
}