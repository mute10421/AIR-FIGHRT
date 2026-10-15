using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// 조종면 애니메이션: 스태빌레이터(피치+롤), 플래퍼론(롤 + 플랩), 앞전 플랩(플랩 + AoA 스케줄), 스피드 브레이크.
    /// 실제 작동기처럼 최대 각속도 제한이 있어서 부드럽게 움직인다.
    /// </summary>
    public class ControlSurfaceFx : MonoBehaviour
    {
        FlightModel fm;
        AircraftFactory.Parts p;
        Transform lefR, lefL, flapR, flapL;
        float stabR, stabL, flR, flL, lef, sb;

        public void Init(FlightModel flight, AircraftFactory.Parts parts)
        {
            fm = flight; p = parts;
            lefR = p.lefR.GetChild(0); lefL = p.lefL.GetChild(0);
            flapR = p.flapR.GetChild(0); flapL = p.flapL.GetChild(0);
        }

        static float Act(float cur, float target, float rateDegPerSec) => Mathf.MoveTowards(cur, target, rateDegPerSec * Time.deltaTime);

        void Update()
        {
            if (fm == null || fm.Destroyed || p == null || p.stabR == null || flapR == null) return;   // (also after a script reload during Play)
            float pitch = Mathf.Clamp(fm.pitchInput, -1f, 1f);
            float roll = Mathf.Clamp(fm.rollInput, -1f, 1f);

            // stabilators: stick back → trailing edge up; differential for roll
            stabR = Act(stabR, pitch * 20f + roll * 8f, 60f);
            stabL = Act(stabL, pitch * 20f - roll * 8f, 60f);
            // flaperons: droop up to 20° with flaps, ±15° differential for roll (right roll → right TE up)
            flR = Act(flR, -fm.flaps * 20f + roll * 15f, 55f);
            flL = Act(flL, -fm.flaps * 20f - roll * 15f, 55f);
            // leading-edge flaps: droop with flaps and on an AoA schedule (like the real F-16)
            lef = Act(lef, Mathf.Clamp(fm.flaps * 15f + Mathf.Clamp(fm.AoA, 0f, 25f) * 0.8f, 0f, 25f), 30f);
            // speed brake petals open 55°
            sb = Act(sb, fm.speedBrake ? 55f : 0f, 45f);

            p.stabR.localRotation = Quaternion.Euler(stabR, 0, 0);
            p.stabL.localRotation = Quaternion.Euler(stabL, 0, 0);
            flapR.localRotation = Quaternion.Euler(flR, 0, 0);
            flapL.localRotation = Quaternion.Euler(flL, 0, 0);
            lefR.localRotation = Quaternion.Euler(lef, 0, 0);
            lefL.localRotation = Quaternion.Euler(lef, 0, 0);
            p.sbR.localRotation = Quaternion.Euler(0, -sb, 0);
            p.sbL.localRotation = Quaternion.Euler(0, sb, 0);
        }
    }
}
