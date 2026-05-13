using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Game.Minimap
{
    public class MinimapRenderer : MonoBehaviour
    {
        private const int BuffLength = 500;

        public static MinimapRenderer Instance;
        public float4 WorldBounds=new float4(-100, -100, 100, 100); // xMin, zMin, xMax, zMax

        [SerializeField] private ComputeShader _computeShader;
        [SerializeField] private RawImage _minimapUI;
        [SerializeField] private int _textureSize = 512;
        [SerializeField] private Camera _minimapCam;
        [SerializeField] private Transform _transMainCam;

        public Texture2D _bakedTerraiTexture;

        private RenderTexture _renderTexture;

        public bool _toggle;
        public GraphicsBuffer _buffer1;
        public GraphicsBuffer _buffer2;
        public int _marshalLength;
        public int _countUnit;

        public bool _dataWrite;

        private ComputeBuffer UnitData;
        private int _kernelIndex;

        public float2 MapOrigin;
        public float MapWidth;
        public float MapHeight;
        public int _previousCount;
        JobHandle j;
        
        void Awake()
        {
            MapWidth = WorldBounds.z - WorldBounds.x;
            MapHeight = WorldBounds.w - WorldBounds.y;
            MapOrigin = WorldBounds.xy;
            _marshalLength = Marshal.SizeOf(typeof(MinimapUnitData));
            _renderTexture = new RenderTexture(_textureSize, _textureSize, 0)
            {
                enableRandomWrite = true
            };
            _renderTexture.Create();
            _minimapUI.texture = _renderTexture;
            
            _bakedTerraiTexture = CaptureRawImage();

            _buffer1 = new GraphicsBuffer(GraphicsBuffer.Target.Structured,GraphicsBuffer.UsageFlags.LockBufferForWrite, BuffLength, Marshal.SizeOf<MinimapUnitData>());
            _buffer2 = new GraphicsBuffer(GraphicsBuffer.Target.Structured,GraphicsBuffer.UsageFlags.LockBufferForWrite, BuffLength, Marshal.SizeOf<MinimapUnitData>());
            //_minimapUI.texture = _bakedTerraiTexture;
            _kernelIndex = _computeShader.FindKernel("RenderMinimap");
            Instance = this;
        }

        void LateUpdate()
        {
            Rendering();
        }

        void OnDestroy()
        {
            _buffer1?.Dispose();
            _buffer2?.Dispose();
        }
        Texture2D CaptureRawImage()
        {
            RenderTexture rt = new RenderTexture(_textureSize, _textureSize, 24);
            _minimapCam.targetTexture = rt;
            
            _minimapCam.enabled = true;
            _minimapCam.Render();
            RenderTexture.active = rt;

            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);

            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();

            _minimapCam.enabled = false;
            RenderTexture.active = null;
            Destroy( rt );

            return tex;
        }
        public GraphicsBuffer GetBuffer(int length)
        {
            _dataWrite = true;
            GraphicsBuffer buffer = _buffer1;
            if (_buffer1==null||_buffer1.count<length)
            {
                int value = length / BuffLength;
                int remain = length % BuffLength;
                _buffer1.Dispose();
                int newlength = (value + 1) * BuffLength;
                _buffer1 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.LockBufferForWrite, length, _marshalLength);
                buffer = _buffer1;
            }
            return buffer;
        }
        public void SetUnitCount(int unitCount)
        {
            _countUnit = unitCount;
            //_dataWrite = false;
        }
        private void Rendering()
        {
            float rotation = _transMainCam.eulerAngles.y * Mathf.Deg2Rad;
            float2 CamPos = new float2(_transMainCam.position.x, _transMainCam.position.z);
            GraphicsBuffer buffer = _buffer1;
            if (_countUnit == 0)
                buffer = HelperBuffer.EmptyBuffer;
            _computeShader.SetBuffer(_kernelIndex, "dataUnit", _buffer1);

            _computeShader.SetFloats("CamUV", CamPos.x, CamPos.y);
            _computeShader.SetFloat("CamRotation", rotation);
            _computeShader.SetInt("UnitCount", _countUnit);
            _computeShader.SetFloats("MapSize", MapWidth, MapHeight);
            _computeShader.SetFloats("MapOrigin", MapOrigin.x, MapOrigin.y);
            _computeShader.SetInt("TextureSize", _textureSize);
            _computeShader.SetTexture(_kernelIndex, "Result", _renderTexture);
            _computeShader.SetTexture(_kernelIndex, "TerrainBase", _bakedTerraiTexture);



            int groups = Mathf.CeilToInt(_textureSize / 8f);
            _computeShader.Dispatch(_kernelIndex, groups, groups, 1);
        }
        public void UpdateMinimapData(EntityQuery query)
        {
            j.Complete();
            

            GraphicsBuffer _read = _toggle ? _buffer1 : _buffer2;
            GraphicsBuffer _write = _toggle ? _buffer2 : _buffer1;

            //Cam Rotation
            float rotation = _transMainCam.eulerAngles.y * Mathf.Deg2Rad;
            float2 CamPos = new float2(_transMainCam.position.x, _transMainCam.position.z);

            if (_previousCount == 0)
                _read = HelperBuffer.EmptyBuffer;
            _computeShader.SetBuffer(_kernelIndex, "dataUnit", _read);

            _computeShader.SetFloats("CamUV", CamPos.x, CamPos.y);
            _computeShader.SetFloat("CamRotation", rotation);
            _computeShader.SetInt("UnitCount", _previousCount);
            _computeShader.SetFloats("MapSize", MapWidth, MapHeight);
            _computeShader.SetFloats("MapOrigin", MapOrigin.x, MapOrigin.y);
            _computeShader.SetInt("TextureSize", _textureSize);
            _computeShader.SetTexture(_kernelIndex, "Result", _renderTexture);
            _computeShader.SetTexture(_kernelIndex, "TerrainBase", _bakedTerraiTexture);

            

            int groups = Mathf.CeilToInt(_textureSize / 8f);
            _computeShader.Dispatch(_kernelIndex, groups, groups, 1);

            int count = query.CalculateChunkCount();
            
            if (_write.count<count)
            {
                _write = new GraphicsBuffer(GraphicsBuffer.Target.Structured,GraphicsBuffer.UsageFlags.LockBufferForWrite, count + (int)(BuffLength * 0.2f), Marshal.SizeOf<MinimapUnitData>());
            }
            
            var job = new SyncMinimapJob()
            {
                UnitDataBuffer = _write.LockBufferForWrite<MinimapUnitData>(0, count),
                WorldBounds = this.WorldBounds,
            };

            j = job.ScheduleParallel(query,default);
            _toggle = !_toggle;
            _previousCount = count;
        }
    }
    public static class HelperBuffer
    {
        private static GraphicsBuffer _emptyBuffer;
        public static GraphicsBuffer EmptyBuffer
        {
            get
            {
                if (_emptyBuffer == null)
                    _emptyBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.LockBufferForWrite
                        , 1, Marshal.SizeOf<MinimapUnitData>());
                return _emptyBuffer;
            }
        }
    }
    public struct UnitUVTeamData
    {
        public float UV;
        public int TeamIndex;
    }
}