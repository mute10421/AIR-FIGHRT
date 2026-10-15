using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace AirFight
{
    /// <summary>
    /// 맨손 추적(XR Hands) 한 손의 상태: 관절 위치, 주먹(그립), 검지 당김(트리거), 엄지 누름(버튼) 판정 + 손 시각화.
    /// 모든 좌표는 trackingSpace 로컬 좌표.
    /// </summary>
    public class TrackedHand
    {
        public const int JointCount = 26;
        public readonly Handedness side;
        public readonly Vector3[] joints = new Vector3[JointCount];
        public bool Tracked { get; private set; }
        public float LostTime { get; private set; }
        public Vector3 Palm { get; private set; }          // smoothed palm centre
        public Vector3 IndexTip => joints[J(XRHandJointID.IndexTip)];
        public float Scale { get; private set; } = 0.09f;  // wrist → middle knuckle (m)
        public float GripCurl { get; private set; }        // 0 open … 1 fist (middle+ring+little)
        public float IndexCurl { get; private set; }
        public bool Fist { get; private set; }
        public bool IndexPulled { get; private set; }
        public bool ThumbPressed { get; private set; }
        public bool ThumbPressedDown { get; private set; }  // edge this frame
        public bool Busy;                                    // holding stick/throttle (no poking)

        LineRenderer[] fingers;
        Transform[] tips;
        bool palmInit;

        public TrackedHand(Handedness h) { side = h; }

        public static int J(XRHandJointID id) => (int)id - (int)XRHandJointID.Wrist;

        public void BuildVisual(Transform parent)
        {
            var root = new GameObject(side + "HandVisual").transform;
            root.SetParent(parent, false);
            fingers = new LineRenderer[5];
            tips = new Transform[5];
            Color skin = new Color(0.85f, 0.7f, 0.6f);
            for (int f = 0; f < 5; f++)
            {
                var lr = Build.Line(root, skin, 0.014f, 5, false);
                lr.numCapVertices = 3; lr.numCornerVertices = 2;
                fingers[f] = lr;
                tips[f] = Build.Prim(PrimitiveType.Sphere, root, Vector3.zero, Vector3.one * 0.016f, skin, true).transform;
            }
        }

        static readonly XRHandJointID[][] Chains =
        {
            new[] { XRHandJointID.Wrist, XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip },
            new[] { XRHandJointID.Wrist, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip },
            new[] { XRHandJointID.Wrist, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip },
            new[] { XRHandJointID.Wrist, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip },
            new[] { XRHandJointID.Wrist, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip },
        };

        public void Update(XRHandSubsystem sub, float dt)
        {
            bool ok = false;
            if (sub != null)
            {
                XRHand hand = side == Handedness.Left ? sub.leftHand : sub.rightHand;
                if (hand.isTracked)
                {
                    ok = true;
                    for (int i = 0; i < JointCount; i++)
                    {
                        var id = (XRHandJointID)(i + (int)XRHandJointID.Wrist);
                        if (hand.GetJoint(id).TryGetPose(out Pose p)) joints[i] = p.position;
                    }
                }
            }
            Tracked = ok;
            LostTime = ok ? 0f : LostTime + dt;
            ThumbPressedDown = false;
            if (!ok) { SetVisual(false); return; }

            Vector3 palmRaw = joints[J(XRHandJointID.Palm)];
            Palm = palmInit ? Vector3.Lerp(Palm, palmRaw, 1f - Mathf.Exp(-dt / 0.04f)) : palmRaw;  // jitter filter
            palmInit = true;
            Scale = Mathf.Max(0.05f, Vector3.Distance(joints[J(XRHandJointID.Wrist)], joints[J(XRHandJointID.MiddleProximal)]));

            float Curl(XRHandJointID tip)
            {
                float d = Vector3.Distance(joints[J(tip)], palmRaw) / Scale;   // ~1.0 open, ~0.35 fist
                return Mathf.Clamp01((1.0f - d) / 0.6f);
            }
            GripCurl = (Curl(XRHandJointID.MiddleTip) + Curl(XRHandJointID.RingTip) + Curl(XRHandJointID.LittleTip)) / 3f;
            IndexCurl = Curl(XRHandJointID.IndexTip);

            // hysteresis
            if (!Fist && GripCurl > 0.62f) Fist = true;
            else if (Fist && GripCurl < 0.42f) Fist = false;
            if (!IndexPulled && IndexCurl > 0.6f) IndexPulled = true;
            else if (IndexPulled && IndexCurl < 0.4f) IndexPulled = false;

            // thumb pressed down onto the side of the index finger (like pressing a button on top of a grip)
            float thumb = Vector3.Distance(joints[J(XRHandJointID.ThumbTip)], joints[J(XRHandJointID.IndexIntermediate)]) / Scale;
            bool before = ThumbPressed;
            if (!ThumbPressed && thumb < 0.28f) ThumbPressed = true;
            else if (ThumbPressed && thumb > 0.42f) ThumbPressed = false;
            ThumbPressedDown = ThumbPressed && !before;

            SetVisual(true);
            for (int f = 0; f < 5; f++)
            {
                for (int k = 0; k < 5; k++) fingers[f].SetPosition(k, joints[J(Chains[f][k])]);
                tips[f].localPosition = joints[J(Chains[f][4])];
            }
        }

        public void Tint(Color c)
        {
            if (fingers == null) return;
            var m = Mats.Get(c, true);
            foreach (var l in fingers) l.sharedMaterial = m;
            foreach (var t in tips) t.GetComponent<Renderer>().sharedMaterial = m;
        }

        void SetVisual(bool on)
        {
            if (fingers == null) return;
            var root = fingers[0].transform.parent.gameObject;
            if (root.activeSelf != on) root.SetActive(on);
        }

        /// <summary>Horizontal direction wrist → middle knuckle, used for stick twist (rudder).</summary>
        public Vector3 HandForward => joints[J(XRHandJointID.MiddleProximal)] - joints[J(XRHandJointID.Wrist)];
    }

    public static class HandSubsystemLocator
    {
        static readonly List<XRHandSubsystem> list = new List<XRHandSubsystem>();
        public static XRHandSubsystem Find()
        {
            list.Clear();
            SubsystemManager.GetSubsystems(list);
            foreach (var s in list) if (s.running) return s;
            return list.Count > 0 ? list[0] : null;
        }
    }
}
