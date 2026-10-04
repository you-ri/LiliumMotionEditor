using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// Timeline が入っているときだけ読み込まれるアセンブリ（defineConstraints: MKT_TIMELINE）。
    /// プレビューに置いた文脈（技の演出など）の Director を**手回しの時計**にして、窓のフレームに合わせて評価する。
    ///
    /// 決まりごと（2026-09-18 のスパイクで確かめた）:
    /// - 時計は窓が正。Director は Manual にして、窓が time を入れて Evaluate する（自分では進まない）。
    /// - prefab が持っているバインドはそのまま使う。**バインドされていないトラックは触らない**
    ///   （自キャラのトラックは実行時にゲームがバインドするもので、ここでバインドすると編集中の姿勢を演出が上書きする）。
    /// - 音のトラックはバインドを外す。編集中に鳴らさないため。
    /// - ゲーム固有のトラック（確定ダメージなど）は未バインドのままにする。バインドすると副作用が走る。
    ///   どのトラックに何を入れるかを変えたいゲームは IContextHook で決める。
    /// </summary>
    [InitializeOnLoad]
    static class ContextProbeTimeline
    {
        static ContextProbeTimeline ()
        {
            ContextProbe.prepare = Prepare;
            ContextProbe.apply = Apply;
            ContextProbe.evaluate = Evaluate;
            ContextProbe.readShots = ReadShots;
            ContextProbe.readExternalClock = ReadExternalClock;
            ContextProbe.readInspectedScene = ReadInspectedScene;
        }

        // ---- 開いているシーンの演出（S15d） ----

        /// <summary>
        /// Unity の Timeline 窓が開いている、開いているシーンの演出を**読むだけ**で取る（Motion Scene 用）。
        /// Director には何も書かない（手回しにしない・バインドを変えない・評価しない）。時刻を動かすのは Timeline 窓で、こちらはそれに付いていく。
        /// バインドされているアニメーションのトラックは Timeline がそのキャラを動かすので、そのクリップとしては編集しない（自キャラのトラックは未バインドで使う。09-19 ユーザー決定）。
        /// prefab の編集画面・プレビューの世界の演出は対象外（null）
        /// </summary>
        static ContextInfo ReadInspectedScene ()
        {
            PlayableDirector director = UnityEditor.Timeline.TimelineEditor.inspectedDirector;
            if (director == null) return null;
            GameObject go = director.gameObject;
            if (EditorUtility.IsPersistent (go) || !go.scene.IsValid () || EditorSceneManager.IsPreviewScene (go.scene)) return null;
            return ReadDirector (director);
        }

        /// <summary>
        /// Director を読むだけで中身（トラック・バインド・クリップの置き方）を取る。Director には何も書かない
        /// </summary>
        internal static ContextInfo ReadDirector (PlayableDirector director)
        {
            TimelineAsset timeline = director != null ? director.playableAsset as TimelineAsset : null;
            if (timeline == null) return null;
            GameObject go = director.gameObject;

            ContextInfo info = new ContextInfo { instance = go, label = go.name + " / " + timeline.name, readOnly = true, duration = timeline.duration };
            if (timeline.editorSettings.frameRate > 0) info.frameRate = timeline.editorSettings.frameRate;
            System.Text.StringBuilder signature = new System.Text.StringBuilder ();
            signature.Append (director.GetHashCode ()).Append ('/').Append (timeline.GetHashCode ()).Append ('/').Append (info.duration).Append ('/').Append (info.frameRate);
            foreach (TrackAsset track in timeline.GetOutputTracks ()) {
                ContextTrack entry = new ContextTrack {
                    name = track.name,
                    typeName = track.GetType ().Name,
                    binding = director.GetGenericBinding (track),
                };
                signature.Append ('|').Append (track.name).Append (':').Append (entry.binding != null ? entry.binding.GetHashCode () : 0).Append (track.mutedInHierarchy ? "m" : "");
                AnimationTrack animationTrack = track as AnimationTrack;
                if (animationTrack != null) {
                    ReadClips (animationTrack, entry);
                    if (entry.binding != null) {
                        entry.editBlock = Tr ("CONTEXT_PROBE_TIMELINE_BOUND_TRACK", track.name, entry.binding.name);
                    }
                    foreach (ContextClip clip in entry.clips) {
                        signature.Append (';').Append (clip.clip != null ? clip.clip.GetHashCode () : 0).Append (',').Append (clip.start).Append (',').Append (clip.duration)
                            .Append (',').Append (clip.clipIn).Append (',').Append (clip.timeScale).Append (',').Append (clip.blendIn).Append (',').Append (clip.blendOut)
                            .Append (',').Append (clip.offsets).Append (clip.overrideTrack ? "o" : "").Append (clip.hasAvatarMask ? "a" : "");
                    }
                }
                info.tracks.Add (entry);
            }
            info.signature = signature.ToString ();
            return info;
        }

        // ---- カメラ割り（S15c） ----

        /// <summary>
        /// 元の prefab の Cinemachine のトラックから、ショットごとのカメラ・注視先・画角を読み、複製の Transform に結び付ける。
        /// Cinemachine の型は参照しない（入っていないプロジェクトでも読み込めるように、名前で読む）。
        /// カメラの部品は複製から外したままにする（Cinemachine はシーンをまたいで登録されるので、開いているシーンのカメラを動かしてしまう）
        /// </summary>
        static void ReadShots (ContextInfo info, GameObject source)
        {
            if (info == null || info.instance == null || source == null) return;
            List<ContextShot> shots = new List<ContextShot> ();
            foreach (PlayableDirector director in source.GetComponentsInChildren<PlayableDirector> (true)) {
                TimelineAsset timeline = director.playableAsset as TimelineAsset;
                if (timeline == null) continue;
                foreach (TrackAsset track in timeline.GetOutputTracks ()) {
                    if (track.GetType ().Name != "CinemachineTrack") continue;
                    if (track.mutedInHierarchy) {
                        info.notes.Add (Tr ("CONTEXT_PROBE_TIMELINE_MUTED_CAMERA_TRACK", track.name));
                        continue;
                    }
                    foreach (TimelineClip clip in track.GetClips ()) {
                        TimelineClip captured = clip;
                        ContextShot shot = new ContextShot {
                            name = clip.displayName,
                            start = clip.start,
                            end = clip.end,
                            weightAt = t => captured.EvaluateMixIn (t),
                        };
                        Component camera = ResolveCamera (clip.asset, director);
                        if (camera != null) {
                            shot.camera = MapToInstance (camera.transform, source.transform, info.instance.transform);
                            Transform lookAt = GetMember (camera, "LookAt") as Transform;
                            if (lookAt != null) shot.lookAt = MapToInstance (lookAt, source.transform, info.instance.transform);
                            object lens = GetMember (camera, "Lens") ?? GetMember (camera, "m_Lens");
                            object fieldOfView = lens != null ? GetMember (lens, "FieldOfView") : null;
                            if (fieldOfView is float && (float)fieldOfView > 0) shot.fieldOfView = (float)fieldOfView;
                        }
                        shots.Add (shot);
                    }
                }
            }
            info.shots.AddRange (shots.OrderBy (s => s.start));

            List<string> unresolved = shots.Where (s => s.camera == null).Select (s => s.name).ToList ();
            if (unresolved.Count > 0) {
                info.notes.Add (Tr ("CONTEXT_PROBE_TIMELINE_UNRESOLVED_SHOTS", unresolved.Count, string.Join (Tr ("CONTEXT_PROBE_TIMELINE_LIST_SEPARATOR"), unresolved)));
            }
        }

        /// <summary>ショットのカメラ（CinemachineShot.VirtualCamera）を元の Director で解決する</summary>
        static Component ResolveCamera (Object shotAsset, PlayableDirector director)
        {
            if (shotAsset == null) return null;
            System.Reflection.FieldInfo field = shotAsset.GetType ().GetField ("VirtualCamera");
            if (field == null) return null;
            object reference = field.GetValue (shotAsset);
            System.Reflection.MethodInfo resolve = reference != null ? reference.GetType ().GetMethod ("Resolve") : null;
            if (resolve == null) return null;
            return resolve.Invoke (reference, new object[] { director }) as Component;
        }

        /// <summary>元の prefab の中の Transform を、複製の中の同じ場所へ写す。prefab の外なら null</summary>
        static Transform MapToInstance (Transform target, Transform sourceRoot, Transform instanceRoot)
        {
            if (target == null) return null;
            if (target == sourceRoot) return instanceRoot;
            if (!target.IsChildOf (sourceRoot)) return null;
            return instanceRoot.Find (AnimationUtility.CalculateTransformPath (target, sourceRoot));
        }

        /// <summary>名前でプロパティかフィールドを読む（公開・非公開とも）</summary>
        static object GetMember (object target, string name)
        {
            if (target == null) return null;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            System.Type type = target.GetType ();
            System.Reflection.PropertyInfo property = type.GetProperty (name, flags);
            if (property != null && property.GetIndexParameters ().Length == 0) return property.GetValue (target);
            System.Reflection.FieldInfo field = type.GetField (name, flags);
            return field != null ? field.GetValue (target) : null;
        }

        // ---- Timeline 窓への追従（S15c） ----

        /// <summary>
        /// Unity の Timeline 窓が同じ演出を開いていれば、その再生位置を読む。
        /// あわせて、自キャラのトラックが未バインドかを両側で確かめる（プレビューの複製でバインドされていると編集中の姿勢を上書きし、
        /// Timeline 窓の側でバインドされているとそちらのキャラが動く）
        /// </summary>
        static ExternalClock ReadExternalClock (ContextInfo info, string selfTrack)
        {
            ExternalClock result = new ExternalClock ();
            List<PlayableDirector> mine = Directors (info != null ? info.instance : null);
            TimelineAsset timeline = mine.Select (d => d.playableAsset as TimelineAsset).FirstOrDefault (t => t != null);
            if (timeline == null) {
                result.note = Tr ("CONTEXT_PROBE_TIMELINE_NO_TIMELINE");
                return result;
            }

            TimelineAsset inspected = UnityEditor.Timeline.TimelineEditor.inspectedAsset;
            PlayableDirector director = UnityEditor.Timeline.TimelineEditor.inspectedDirector;
            if (inspected == null) {
                result.note = Tr ("CONTEXT_PROBE_TIMELINE_NOT_OPEN", timeline.name);
                return result;
            }
            if (inspected != timeline) {
                result.note = Tr ("CONTEXT_PROBE_TIMELINE_OTHER_OPEN", inspected.name);
                return result;
            }
            if (director == null) {
                result.note = Tr ("CONTEXT_PROBE_TIMELINE_ASSET_ONLY");
                return result;
            }

            result.available = true;
            result.time = director.time;
            if (string.IsNullOrEmpty (selfTrack)) return result;
            // 開いているシーンの演出を読んだだけのとき（S15d）は、自分の側とTimeline 窓の側が同じ Director。バインドは段が編集を止める
            if (info.readOnly) return result;

            foreach (PlayableDirector own in mine) {
                foreach (TrackAsset track in timeline.GetOutputTracks ()) {
                    if (track.name != selfTrack) continue;
                    Object bound = own.GetGenericBinding (track);
                    if (bound != null) {
                        result.note = Tr ("CONTEXT_PROBE_TIMELINE_SELF_BOUND_PREVIEW", selfTrack, bound.name);
                        result.warning = true;
                        return result;
                    }
                }
            }
            foreach (TrackAsset track in timeline.GetOutputTracks ()) {
                if (track.name != selfTrack) continue;
                Object bound = director.GetGenericBinding (track);
                if (bound != null) {
                    result.note = Tr ("CONTEXT_PROBE_TIMELINE_SELF_BOUND_WINDOW", selfTrack, bound.name);
                    result.warning = true;
                }
            }
            return result;
        }

        /// <summary>
        /// 中身を読み、Director を手回しにする。バインドはまだ入れない（拡張点が触った後に Apply で入れる）
        /// </summary>
        static ContextInfo Prepare (GameObject instance)
        {
            ContextInfo info = new ContextInfo { instance = instance, label = instance.name };
            List<PlayableDirector> directors = Directors (instance);
            if (directors.Count == 0) {
                info.notes.Add (Tr ("CONTEXT_PROBE_TIMELINE_NO_DIRECTOR"));
                return info;
            }

            foreach (PlayableDirector director in directors) {
                director.playOnAwake = false;
                director.timeUpdateMode = DirectorUpdateMode.Manual;
                TimelineAsset timeline = director.playableAsset as TimelineAsset;
                if (timeline == null) {
                    info.notes.Add (Tr ("CONTEXT_PROBE_TIMELINE_NOT_TIMELINE", director.name));
                    continue;
                }
                info.duration = System.Math.Max (info.duration, timeline.duration);
                if (timeline.editorSettings.frameRate > 0) info.frameRate = timeline.editorSettings.frameRate;
                foreach (TrackAsset track in timeline.GetOutputTracks ()) {
                    ContextTrack entry = new ContextTrack {
                        name = track.name,
                        typeName = track.GetType ().Name,
                        binding = director.GetGenericBinding (track),
                    };
                    if (track is AudioTrack && entry.binding != null) {
                        entry.Bind (null);
                        entry.note = Tr ("CONTEXT_PROBE_TIMELINE_AUDIO_UNBOUND");
                    }
                    AnimationTrack animationTrack = track as AnimationTrack;
                    if (animationTrack != null) ReadClips (animationTrack, entry);
                    info.tracks.Add (entry);
                }
            }
            return info;
        }

        /// <summary>
        /// アニメーションのトラックのクリップを読む（S15b の段が、演出の時刻 ↔ クリップの時刻の写像とブレンドを知るため）
        /// </summary>
        static void ReadClips (AnimationTrack track, ContextTrack entry)
        {
            // 「シーンのオフセット」のトラックは、トラックに残っている位置・回転を使わない
            string trackOffsets = track.trackOffset != TrackOffset.ApplySceneOffsets ? DescribeOffsets (track.position, track.rotation) : null;
            int index = 0;
            // 番号は選択の保存に使う。GetClips の並びはグラフを作り直すと開始順に並べ替わるので、最初から開始順で振る
            foreach (TimelineClip timelineClip in track.GetClips ().OrderBy (c => c.start)) {
                AnimationPlayableAsset asset = timelineClip.asset as AnimationPlayableAsset;
                TimelineClip captured = timelineClip;
                string clipOffsets = asset != null ? DescribeOffsets (asset.position, asset.rotation) : null;
                string offsets = null;
                if (trackOffsets != null) offsets = Tr ("CONTEXT_PROBE_TIMELINE_TRACK_OFFSETS", trackOffsets, track.trackOffset);
                if (clipOffsets != null) {
                    string clipText = Tr ("CONTEXT_PROBE_TIMELINE_CLIP_OFFSETS", clipOffsets);
                    offsets = offsets != null ? Tr ("CONTEXT_PROBE_TIMELINE_JOIN", offsets, clipText) : clipText;
                }
                entry.clips.Add (new ContextClip {
                    track = entry,
                    index = index++,
                    clip = asset != null ? asset.clip : null,
                    start = timelineClip.start,
                    duration = timelineClip.duration,
                    clipIn = timelineClip.clipIn,
                    timeScale = timelineClip.timeScale,
                    blendIn = timelineClip.mixInDuration,
                    blendOut = timelineClip.mixOutDuration,
                    weightAt = t => captured.EvaluateMixIn (t) * captured.EvaluateMixOut (t),
                    muted = track.muted,
                    overrideTrack = track.parent is AnimationTrack,
                    hasAvatarMask = track.applyAvatarMask && track.avatarMask != null,
                    offsets = offsets,
                });
            }
        }

        /// <summary>位置・回転のオフセットが恒等でなければ説明を返す</summary>
        static string DescribeOffsets (Vector3 position, Quaternion rotation)
        {
            bool moved = position.sqrMagnitude > 1e-8f;
            bool turned = Quaternion.Angle (Quaternion.identity, rotation) > 0.01f;
            if (!moved && !turned) return null;
            string text = moved ? Tr ("CONTEXT_PROBE_TIMELINE_POSITION", position.ToString ("F2")) : "";
            if (turned) {
                string rotationText = Tr ("CONTEXT_PROBE_TIMELINE_ROTATION", rotation.eulerAngles.ToString ("F0"));
                text = moved ? Tr ("CONTEXT_PROBE_TIMELINE_JOIN", text, rotationText) : rotationText;
            }
            return text;
        }

        /// <summary>
        /// 拡張点が触った後のバインドを入れて、グラフを作り直す
        /// </summary>
        static void Apply (ContextInfo info)
        {
            if (info == null || info.instance == null) return;
            foreach (PlayableDirector director in Directors (info.instance)) {
                TimelineAsset timeline = director.playableAsset as TimelineAsset;
                if (timeline == null) continue;
                foreach (TrackAsset track in timeline.GetOutputTracks ()) {
                    ContextTrack entry = info.tracks.FirstOrDefault (t => t.rebind && t.name == track.name && t.typeName == track.GetType ().Name);
                    if (entry == null) continue;
                    director.SetGenericBinding (track, entry.binding);
                    if (!string.IsNullOrEmpty (entry.note)) info.notes.Add (track.name + ": " + entry.note);
                }
                director.RebuildGraph ();
            }
        }

        /// <summary>
        /// その時刻（秒）の姿にする。演出の外（負の時刻・終わりより後）は端で止める
        /// </summary>
        static void Evaluate (GameObject instance, double time)
        {
            foreach (PlayableDirector director in Directors (instance)) {
                if (director.playableAsset == null) continue;
                director.time = System.Math.Max (0, System.Math.Min (time, director.duration));
                director.Evaluate ();
            }
        }

        static List<PlayableDirector> Directors (GameObject instance)
        {
            List<PlayableDirector> result = new List<PlayableDirector> ();
            if (instance == null) return result;
            foreach (PlayableDirector director in instance.GetComponentsInChildren<PlayableDirector> (true)) {
                if (director != null) result.Add (director);
            }
            return result;
        }
    }

}
