using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using RoR2;
using RoR2.UI;
using UnityEngine;
using UnityEngine.UI;
using static R2API.SoundAPI;
using GameOverController = On.RoR2.GameOverController;
using HUD = On.RoR2.UI.HUD;
using Random = UnityEngine.Random;


/* TODO:
 * [X]VOLUME
 * [ ]Add volume slider in-game using 'Risk Of Options'
 * [ ]Add More Songs
 * [ ]Add slight screen bounce zoom on beat
 * [ ]Add Rave Lasers to Teleporter
 * [ ]Remove OG music while playing
 * [ ]Add Support for songs in Moisture Upset
 */

namespace RiskOfRave {
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class RiskOfRave : BaseUnityPlugin {
        private const string ModGuid = "com.RuneFoxMods.RiskOfRave";
        private const string ModName = "RiskOfRave";
        private const string ModVersion = "1.0.7";

        public float lastBeat;
        private const float Alpha = 0.1f;

        private readonly Conductor _conductor = new();
        private HoldoutZoneController _hodl;
        private ObjectivePanelController.ObjectiveTracker _hodlTracker;

        private float _lastHue;
        private MusicController _musicCon;

        private GameObject _raveTint;
        private Image _raveTintImg;
        private RectTransform _raveTintRect;

        private static ConfigEntry<int> Volume { get; set; }

        public void Awake() {
            RoR2Application.isModded = true;

            Volume = Config.Bind("Config", "Volume", 100, "How loud the music will be on a scale from 0-100");

            //load the rave music into sound banks
            using var bankStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RiskOfRave.Rave.bnk");
            if (bankStream == null)
                throw new Exception("Failed to load Sound Bank");
            var bytes = new byte[bankStream.Length];
            var toRead = bytes.Length;
            while (toRead > 0)
                toRead -= bankStream.Read(bytes, bytes.Length - toRead, toRead);
            SoundBanks.Add(bytes);

            On.RoR2.HoldoutZoneController.OnEnable += StartRaveTest;
            On.RoR2.HoldoutZoneController.OnDisable += EndRaveTest;
            GameOverController.SetRunReport += EndRaveDeath;

            HUD.Awake += RaveUI;

            On.RoR2.UI.ObjectivePanelController.AddObjectiveTracker += (orig, self, tracker) => {
                orig(self, tracker);
                if (tracker.ToString() == "RoR2.HoldoutZoneController+ChargeHoldoutZoneObjectiveTracker")
                    _hodlTracker = tracker;
            };

            On.RoR2.UI.ObjectivePanelController.RemoveObjectiveTracker += (orig, self, tracker) => {
                orig(self, tracker);
                if (tracker.ToString() == "RoR2.HoldoutZoneController+ChargeHoldoutZoneObjectiveTracker")
                    _hodlTracker = null;
            };
        }

        //TODO: create a prefab of a image that is scaled across the entire screen and load it in

        public void Update() {
            if (_conductor != null) {
                _conductor.UpdateConductor();

                if (_conductor.SongPosition >= lastBeat + Conductor.Crochet && _conductor.IsPlaying) {
                    //do the thing every beat
                    _lastHue += Random.Range(0.25f, 0.75f);
                    _lastHue = Mathf.Repeat(_lastHue, 1f);
                    var newColor = Color.HSVToRGB(_lastHue, 1f, 0.5f);

                    if (_raveTint is not null && _conductor.SongPosition >= 1.1f)
                        _raveTintImg.color = new Color(newColor.a, newColor.g, newColor.b, Alpha);

                    lastBeat += Conductor.Crochet;
                } else if (!_conductor.IsPlaying) {
                    //having some issue clearing the color, this should fix it
                    if (_raveTint is not null)
                        _raveTintImg.color = new Color(1, 1, 1, 0);
                }
            }

            if (_musicCon is null) {
                var con = FindFirstObjectByType<MusicController>();
                if (con)
                    _musicCon = con;
            }

            if (_hodlTracker == null || !_hodl) return;
            var local = LocalUserManager.GetFirstLocalUser();
            var charging = _hodl.IsBodyInChargingRadius(local.cachedBody);
            AkSoundEngine.SetRTPCValue("inNOut", charging ? 0 : 1);
            AkSoundEngine.SetRTPCValue("RaveVolume", Volume.Value);
        }

        private void EndRaveTest(On.RoR2.HoldoutZoneController.orig_OnDisable orig, HoldoutZoneController self) {
            EndRave();
            orig(self);
            _hodl = null;
        }

        private void RaveUI(HUD.orig_Awake orig, RoR2.UI.HUD self) {
            orig(self);

            _raveTint = new GameObject { name = "RaveTint" };
            _raveTintRect = _raveTint.AddComponent<RectTransform>();
            _raveTintRect.parent = self.mainContainer.transform;

            _raveTintRect.anchorMax = Vector2.one;
            _raveTintRect.anchorMin = Vector2.zero;
            _raveTintRect.localScale = new Vector3(10, 10, 10);
            _raveTintRect.anchoredPosition = Vector2.zero;
            _raveTintImg = _raveTint.AddComponent<Image>();
            _raveTintImg.color = new Color(1, 1, 1, 0f);
            _raveTintImg.raycastTarget = false;
        }

        private void StartRaveTest(On.RoR2.HoldoutZoneController.orig_OnEnable orig, HoldoutZoneController self) {
            orig(self);
            
            _hodl = self;
            if (_musicCon)
                AkSoundEngine.PostEvent("RaveStart", _musicCon.gameObject);
            _conductor.StartConductor();
        }

        private void EndRaveDeath(GameOverController.orig_SetRunReport orig, RoR2.GameOverController self, RunReport newRunReport) {
            EndRave();
            orig(self, newRunReport);
        }

        private void EndRave() {
            _hodl = null;
            
            if (_musicCon) {
                AkSoundEngine.PostEvent("RaveStop", _musicCon.gameObject);
            }

            _raveTintImg.color = new Color(1, 1, 1, 0);
            _conductor.StopConductor();
            lastBeat = 0;
        }

        private class Conductor {
            private const float Bpm = 165; //bpm of the song
            private const float Offset = 0.4f; //mp3s usually have a tiny gap at beginning, this is to help w/ that
            public const float Crochet = 60f / Bpm; //time duration of a beat. calculated from bpm
            private float _dspTimeSongStart; //the dspTime that the song started at;
            public bool IsPlaying;
            public float SongPosition; //position of song in dspTime, updates every frame

            public void UpdateConductor() {
                if (IsPlaying)
                    SongPosition = Time.time - _dspTimeSongStart - Offset;
            }

            public void StartConductor() {
                _dspTimeSongStart = Time.time;
                IsPlaying = true;
            }

            public void StopConductor() => IsPlaying = false;
        }
    }
}
