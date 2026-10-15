#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace AirFight.EditorTools
{
    /// <summary>Android XR 맨손 추적 권한(android.permission.HAND_TRACKING)을 빌드 매니페스트에 추가.</summary>
    public class HandTrackingPermission : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        public int callbackOrder => 999;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath)) return;
            var doc = new XmlDocument();
            doc.Load(manifestPath);
            var manifest = doc.DocumentElement;
            AddPermission(doc, manifest, "android.permission.HAND_TRACKING");
            doc.Save(manifestPath);
        }

        static void AddPermission(XmlDocument doc, XmlElement manifest, string name)
        {
            foreach (XmlNode n in manifest.SelectNodes("uses-permission"))
                if (n.Attributes?["android:name"]?.Value == name) return;
            var el = doc.CreateElement("uses-permission");
            el.SetAttribute("name", AndroidNs, name);
            manifest.AppendChild(el);
        }
    }
}
#endif
