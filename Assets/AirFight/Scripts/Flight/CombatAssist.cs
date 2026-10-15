using System;
using System.Collections.Generic;
using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// 추적 보정: 적기가 기수 앞 ±20° · 4 km 안에 있으면 기수를 적의 사격 리드 지점 쪽으로 살짝(최대 5°/s) 돌려 준다.
    /// 조종사가 반대 방향으로 스틱을 크게 밀면 보정이 줄어든다. 지상·자동착륙·사망 시 꺼짐.
    /// </summary>
    [DefaultExecutionOrder(50)]   // after FlightModel's fly-by-wire step
    public class CombatAssist : MonoBehaviour
    {
        public FlightModel fm;
        public Weapons weapons;
        public Damageable self;
        public Func<IEnumerable<Damageable>> targets;
        public float cone = 20f;          // deg
        public float range = 4000f;       // m
        public float maxRate = 5f;        // deg/s extra turn toward the lead point
        public Damageable Current { get; private set; }

        void FixedUpdate()
        {
            Current = null;
            if (fm == null || fm.Destroyed || fm.onGround || fm.ExternalControl || (self && self.Dead) || targets == null) return;
            var rb = fm.Body;
            Vector3 pos = rb.position, fwd = transform.forward, myVel = rb.linearVelocity;

            // prefer the missile seeker's target, else the target closest to the nose
            Damageable seek = weapons && weapons.SeekerTarget && !weapons.SeekerTarget.Dead ? weapons.SeekerTarget : null;
            Damageable best = null; Vector3 bestDir = Vector3.zero; float bestAng = cone;
            foreach (var d in targets())
            {
                if (d == null || d.Dead || (self && d.team == self.team)) continue;
                if (seek != null && d != seek) continue;
                Vector3 to = d.transform.position - pos;
                float dist = to.magnitude;
                if (dist > range || dist < 60f) continue;
                Vector3 tv = Damageable.VelocityOf(d);
                float t = dist / Mathf.Max(600f, (weapons ? weapons.muzzleVelocity : 1030f) + Vector3.Dot(myVel, to / dist));
                Vector3 dir = (d.transform.position + (tv - myVel) * t - pos).normalized;
                float ang = Vector3.Angle(fwd, dir);
                if (ang < bestAng) { bestAng = ang; bestDir = dir; best = d; }
            }
            if (best == null) return;
            Current = best;

            // the pilot keeps authority: pulling hard away reduces the help
            float pilot = Mathf.Clamp01(1f - (Mathf.Abs(fm.pitchInput) + Mathf.Abs(fm.rollInput)) * 0.4f) * 0.6f + 0.4f;
            // strongest in the middle of the cone, fading out at its edge
            float rate = Mathf.Min(bestAng * 0.8f, maxRate) * pilot * Mathf.Lerp(0.35f, 1f, Mathf.InverseLerp(cone, cone * 0.5f, bestAng));
            Vector3 axis = Vector3.Cross(fwd, bestDir);
            if (axis.sqrMagnitude < 1e-8f) return;
            Vector3 extra = axis.normalized * (rate * Mathf.Deg2Rad);
            rb.angularVelocity += extra;
            // turn the flight path with the nose so the help is felt immediately (like a little extra lift)
            rb.linearVelocity = Quaternion.AngleAxis(rate * 0.6f * Time.fixedDeltaTime, axis.normalized) * rb.linearVelocity;
        }
    }
}
