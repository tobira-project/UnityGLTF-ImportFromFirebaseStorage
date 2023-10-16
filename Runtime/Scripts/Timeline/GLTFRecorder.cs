#define USE_ANIMATION_POINTER

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GLTF.Schema;
using Unity.Profiling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityGLTF.Timeline
{
	public class GLTFRecorder
	{
		public GLTFRecorder(Transform root, bool recordBlendShapes = true, bool recordRootInWorldSpace = false, bool recordAnimationPointer = false)
		{
			if (!root)
				throw new ArgumentNullException(nameof(root), "Please provide a root transform to record.");

			this.root = root;
			this.recordBlendShapes = recordBlendShapes;
			this.recordRootInWorldSpace = recordRootInWorldSpace;
			this.recordAnimationPointer = recordAnimationPointer;
		}

		/// <summary>
		/// Optionally assign a list of transforms to be recorded, other transforms will be ignored
		/// </summary>
		internal ICollection<Transform> recordingList = null;
		private bool AllowRecordingTransform(Transform tr) => recordingList == null || recordingList.Contains(tr);

		private Transform root;
		private Dictionary<Transform, AnimationData> data = new Dictionary<Transform, AnimationData>(64);
		private double startTime;
		private double lastRecordedTime;
		private bool hasRecording;
		private bool isRecording;
		
		private readonly bool recordBlendShapes;
		private readonly bool recordRootInWorldSpace;
		private readonly bool recordAnimationPointer;

		public bool HasRecording => hasRecording;
		public bool IsRecording => isRecording;

		public double LastRecordedTime => lastRecordedTime;

		public string AnimationName = "Recording";

		public delegate void OnBeforeAddAnimationDataDelegate(AnimationDataArgs animationData);
		public delegate void OnPostExportDelegate(PostExportArgs animationData);
		
		/// <summary>
		/// Callback to modify the animation data before it is added to the animation.
		/// Is called once for each track after the recording has ended. 
		/// </summary>
		public OnBeforeAddAnimationDataDelegate OnBeforeAddAnimationData;
		
		/// <summary>
		/// Callback to modify or add additional data to the gltf root after the recording has ended and animation
		/// data is added to the animation.
		/// </summary>
		public OnPostExportDelegate OnPostExport;


		public class PostExportArgs
		{
			public Bounds AnimationTranslationBounds { get; private set; }
			public GLTFSceneExporter Exporter { get; private set; }
			public GLTFRoot GltfRoot { get; private set; }		
			
			internal PostExportArgs(Bounds animationTranslationBounds, GLTFSceneExporter exporter, GLTFRoot gltfRoot)
			{
				this.AnimationTranslationBounds = animationTranslationBounds;
				this.Exporter = exporter;
				this.GltfRoot = gltfRoot;
			}
		}

		public class AnimationDataArgs
		{
			public Object AnimatedObject => plan.GetTarget(tr.tr);
			public string PropertyName => plan.propertyName;

			private AnimationData tr;
			private AnimationData.ExportPlan plan;
			
			public readonly Dictionary<double, object> Samples;
			
			internal AnimationDataArgs( AnimationData.ExportPlan plan, AnimationData tr, Dictionary<double, object> samples)
			{
				this.plan = plan;
				this.tr = tr;
				this.Samples = samples;
			}
		}
		
		internal class AnimationData
		{
			internal Transform tr;
			private SkinnedMeshRenderer smr;
			private bool recordBlendShapes;
			private bool inWorldSpace = false;
			private bool recordAnimationPointer;

#if USE_ANIMATION_POINTER
			private static List<ExportPlan> exportPlans;
			private static MaterialPropertyBlock materialPropertyBlock = new MaterialPropertyBlock();
			internal List<Track> tracks = new List<Track>();

			internal class ExportPlan
			{
				public string propertyName;
				public Type dataType;
				public Func<Transform, Object> GetTarget;
				public Func<Transform, Object, AnimationData, object> GetData;

				public ExportPlan(string propertyName, Type dataType, Func<Transform, Object> GetTarget, Func<Transform, Object, AnimationData, object> GetData)
				{
					this.propertyName = propertyName;
					this.dataType = dataType;
					this.GetTarget = GetTarget;
					this.GetData = GetData;
				}

				public object Sample(AnimationData data)
				{
					var target = GetTarget(data.tr);
					return GetData(data.tr, target, data);
				}
			}
#endif

			public AnimationData(Transform tr, double time, bool zeroScale = false, bool recordBlendShapes = true, bool inWorldSpace = false, bool recordAnimationPointer = false)
			{
				this.tr = tr;
				this.smr = tr.GetComponent<SkinnedMeshRenderer>();
				this.recordBlendShapes = recordBlendShapes;
				this.inWorldSpace = inWorldSpace;
				this.recordAnimationPointer = recordAnimationPointer;

				if (exportPlans == null)
				{
					exportPlans = new List<ExportPlan>();
					exportPlans.Add(new ExportPlan("translation", typeof(Vector3), x => x, (tr0, _, options) => options.inWorldSpace ? tr0.position : tr0.localPosition));
					exportPlans.Add(new ExportPlan("rotation", typeof(Quaternion), x => x, (tr0, _, options) =>
					{
						var q = options.inWorldSpace ? tr0.rotation : tr0.localRotation;
						return new Quaternion(q.x, q.y, q.z, q.w);
					}));
					exportPlans.Add(new ExportPlan("scale", typeof(Vector3), x => x, (tr0, _, options) => options.inWorldSpace ? tr0.lossyScale : tr0.localScale));

					if (recordBlendShapes)
					{
						exportPlans.Add(new ExportPlan("weights", typeof(float[]),
							x => x.GetComponent<SkinnedMeshRenderer>(), (tr0, x, options) =>
							{
								if (x is SkinnedMeshRenderer skinnedMesh && skinnedMesh.sharedMesh)
								{
									var mesh = skinnedMesh.sharedMesh;
									var blendShapeCount = mesh.blendShapeCount;
									if (blendShapeCount == 0) return null;
									var weights = new float[blendShapeCount];
									for (var i = 0; i < blendShapeCount; i++)
										weights[i] = skinnedMesh.GetBlendShapeWeight(i);
									return weights;
								}

								return null;
							}));
					}

					if (recordAnimationPointer)
					{
						// TODO add other animation pointer export plans

						exportPlans.Add(new ExportPlan("baseColorFactor", typeof(Color), x => x.GetComponent<MeshRenderer>() ? x.GetComponent<MeshRenderer>().sharedMaterial : null, (tr0, mat, options) =>
						{
							var r = tr0.GetComponent<Renderer>();

							if (r.HasPropertyBlock())
							{
								r.GetPropertyBlock(materialPropertyBlock);
	#if UNITY_2021_1_OR_NEWER
								if (materialPropertyBlock.HasColor("_BaseColor")) return materialPropertyBlock.GetColor("_BaseColor").linear;
								if (materialPropertyBlock.HasColor("_Color")) return materialPropertyBlock.GetColor("_Color").linear;
								if (materialPropertyBlock.HasColor("baseColorFactor")) return materialPropertyBlock.GetColor("baseColorFactor").linear;
	#else
								var c = materialPropertyBlock.GetColor("_BaseColor");
								if (c.r != 0 || c.g != 0 || c.b != 0 || c.a != 0) return c;
								c = materialPropertyBlock.GetColor("_Color");
								if (c.r != 0 || c.g != 0 || c.b != 0 || c.a != 0) return c;
								// this leaves an edge case where someone is actually animating color to black:
								// in that case, the un-animated color would now be exported...
	#endif
							}

							if (mat is Material m && m)
							{
								if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor").linear;
								if (m.HasProperty("_Color")) return m.GetColor("_Color").linear;
								if (m.HasProperty("baseColorFactor")) return m.GetColor("baseColorFactor").linear;
							}

							return null;
						}));
					}
				}

				foreach (var plan in exportPlans)
				{
					if (plan.GetTarget(tr)) {
						tracks.Add(new Track(this, plan, time));
					}
				}
			}

			internal class Track
			{
				public Object animatedObject => plan.GetTarget(tr.tr);
				public string propertyName => plan.propertyName;
				// TODO sample as floats?
				public float[] times => samples.Keys.Select(x => (float) x).ToArray();
				public object[] values => samples.Values.ToArray();

				private AnimationData tr;
				private ExportPlan plan;
				private Dictionary<double, object> samples;
				private object lastSample;

				internal AnimationDataArgs GetAnimationDataArgs()
				{
					return new AnimationDataArgs(plan, tr, samples);
				} 
				
				public Track(AnimationData tr, ExportPlan plan, double time)
				{
					this.tr = tr;
					this.plan = plan;
					samples = new Dictionary<double, object>();
					SampleIfChanged(time);
				}

				public void SampleIfChanged(double time)
				{
					var value = plan.Sample(tr);
					if (value == null || (value is Object o && !o))
						return;
					if (!value.Equals(lastSample))
					{
						samples[time] = value;
						lastSample = value;
					}
				}
				
			}

			public void Update(double time)
			{
				foreach (var track in tracks)
				{
					track.SampleIfChanged(time);
				}
			}
		}

		public void StartRecording(double time)
		{
			startTime = time;
			lastRecordedTime = time;
			var trs = root.GetComponentsInChildren<Transform>(true);
			data.Clear();

			foreach (var tr in trs)
			{
				if (!AllowRecordingTransform(tr)) continue;
				data.Add(tr, new AnimationData(tr, 0, !tr.gameObject.activeSelf, recordBlendShapes, recordRootInWorldSpace && tr == root, recordAnimationPointer));
			}

			isRecording = true;
			hasRecording = true;
		}

		public void UpdateRecording(double time)
		{
			if (!isRecording)
			{
				throw new InvalidOperationException($"{nameof(GLTFRecorder)} isn't recording, but {nameof(UpdateRecording)} was called. This is invalid.");
			}

			if (time <= lastRecordedTime)
			{
				Debug.LogWarning("Can't record backwards in time, please avoid this.");
				return;
			}

			var currentTime = time - startTime;
			var trs = root.GetComponentsInChildren<Transform>(true);
			foreach (var tr in trs)
			{
				if (!AllowRecordingTransform(tr)) continue;
				if (!data.ContainsKey(tr))
				{
					// insert "empty" frame with scale=0,0,0 because this object might have just appeared in this frame
					var emptyData = new AnimationData(tr, lastRecordedTime, true, recordBlendShapes, recordAnimationPointer);
					data.Add(tr, emptyData);
					// insert actual keyframe
					data[tr].Update(currentTime);
				}
				else
					data[tr].Update(currentTime);
			}

			lastRecordedTime = time;
		}
		
		internal void EndRecording(out Dictionary<Transform, AnimationData> param)
		{
			param = null;
			if (!hasRecording) return;
			param = data;
		}

		public void EndRecording(string filename, string sceneName = "scene", GLTFSettings settings = null)
		{
			if (!hasRecording) return;

			var dir = Path.GetDirectoryName(filename);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
			using (var filestream = new FileStream(filename, FileMode.Create, FileAccess.Write))
			{
				EndRecording(filestream, sceneName, settings);
			}
		}

		public void EndRecording(Stream stream, string sceneName = "scene", GLTFSettings settings = null)
		{
			if (!hasRecording) return;
			isRecording = false;
			Debug.Log("Gltf Recording saved. Tracks: " + data.Count + ", Total Keyframes: " + data.Sum(x => x.Value.tracks.Sum(y => y.values.Count())));

			if (!settings)
			{
				var adjustedSettings = Object.Instantiate(GLTFSettings.GetOrCreateSettings());
				adjustedSettings.ExportDisabledGameObjects = true;
				adjustedSettings.ExportAnimations = false;
				settings = adjustedSettings;
			}

			if (!recordBlendShapes)
					settings.BlendShapeExportProperties = GLTFSettings.BlendShapeExportPropertyFlags.None;

			var logHandler = new StringBuilderLogHandler();

			var exporter = new GLTFSceneExporter(new Transform[] { root }, new ExportOptions(settings)
			{
				AfterSceneExport = PostExport,
				logger = new Logger(logHandler),
			});

			exporter.SaveGLBToStream(stream, sceneName);

			logHandler.LogAndClear();
		}

		private void PostExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			// this would include animation from the original root
			// exporter.ExportAnimationFromNode(ref root);

			GLTFAnimation anim = new GLTFAnimation();
			anim.Name = AnimationName;

			CollectAndProcessAnimation(exporter, anim, true, out Bounds translationBounds);

			if (anim.Channels.Count > 0 && anim.Samplers.Count > 0)
				gltfRoot.Animations.Add(anim);
			
			OnPostExport?.Invoke( new PostExportArgs(translationBounds, exporter, gltfRoot));
		}

		private static void SafeDestroy(Object obj)
		{
			if (Application.isEditor)
				Object.DestroyImmediate(obj);
			else
				Object.Destroy(obj);
		}

		private static ProfilerMarker processAnimationMarker = new ProfilerMarker("Process Animation");
		private static ProfilerMarker simplifyKeyframesMarker = new ProfilerMarker("Simplify Keyframes");
		private static ProfilerMarker convertValuesMarker = new ProfilerMarker("Convert Values to Arrays");

		private void CollectAndProcessAnimation(GLTFSceneExporter gltfSceneExporter, GLTFAnimation anim, bool calculateTranslationBounds, out Bounds translationBounds)
		{
			var gotFirstValue = false;
			translationBounds = new Bounds();

			foreach (var kvp in data)
			{
				processAnimationMarker.Begin();
				foreach (var tr in kvp.Value.tracks)
				{
					if (tr.times.Length == 0) continue;

					OnBeforeAddAnimationData?.Invoke(tr.GetAnimationDataArgs());
					
					float[] times = tr.times;
					object[] values = tr.values;

					if (calculateTranslationBounds && tr.propertyName == "translation")
					{
						for (var i = 0; i < values.Length; i++)
						{
							var vec = (Vector3) values[i];
							if (!gotFirstValue)
							{
								translationBounds = new Bounds(vec, Vector3.zero);
								gotFirstValue = true;
							}
							else
							{
								translationBounds.Encapsulate(vec);
							}
						}
					}

					gltfSceneExporter.RemoveUnneededKeyframes(ref times, ref values);
					gltfSceneExporter.AddAnimationData(tr.animatedObject, tr.propertyName, anim, times, values);
				}
				processAnimationMarker.End();
			}
		}

		private class StringBuilderLogHandler : ILogHandler
		{
			private readonly StringBuilder sb = new StringBuilder();

			private string LogTypeToLog(LogType logType)
			{
#if UNITY_EDITOR
				// create strings with <color> tags
				switch (logType)
				{
					case LogType.Error:
						return "<color=red>[" + logType + "]</color>";
					case LogType.Assert:
						return "<color=red>[" + logType + "]</color>";
					case LogType.Warning:
						return "<color=yellow>[" + logType + "]</color>";
					case LogType.Log:
						return "[" + logType + "]";
					case LogType.Exception:
						return "<color=red>[" + logType + "]</color>";
					default:
						return "[" + logType + "]";
				}
#else
				return "[" + logType + "]";
#endif
			}

			public void LogFormat(LogType logType, Object context, string format, params object[] args) => sb.AppendLine($"{LogTypeToLog(logType)} {string.Format(format, args)} [Context: {context}]");
			public void LogException(Exception exception, Object context) => sb.AppendLine($"{LogTypeToLog(LogType.Exception)} {exception} [Context: {context}]");

			public void LogAndClear()
			{
				if(sb.Length > 0)
				{
					var str = sb.ToString();
#if UNITY_2019_1_OR_NEWER
					var logType = LogType.Log;
#if UNITY_EDITOR
					if (str.IndexOf("[Error]", StringComparison.Ordinal) > -1 ||
					    str.IndexOf("[Exception]", StringComparison.Ordinal) > -1 ||
					    str.IndexOf("[Assert]", StringComparison.Ordinal) > -1)
						logType = LogType.Error;
					else if (str.IndexOf("[Warning]", StringComparison.Ordinal) > -1)
						logType = LogType.Warning;
#endif
					Debug.LogFormat(logType, LogOption.NoStacktrace, null, "Export Messages:" + "\n{0}", sb.ToString());
#else
					Debug.Log(string.Format("Export Messages:" + "\n{0}", str));
#endif
				}
				sb.Clear();
			}
		}
	}
}
