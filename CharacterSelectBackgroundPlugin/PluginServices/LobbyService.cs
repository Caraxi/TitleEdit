using CharacterSelectBackgroundPlugin.Data;
using CharacterSelectBackgroundPlugin.Data.Layout;
using CharacterSelectBackgroundPlugin.Utils;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CharacterSelectBackgroundPlugin.PluginServices
{
    public unsafe class LobbyService : IDisposable
    {

        [Signature("C6 81 ?? ?? ?? ?? ?? 8B 02 89 41 60")]
        private readonly delegate* unmanaged<LobbyCamera*, float[], float[], float, IntPtr> fixOnNative = null!;
        [Signature("40 53 48 83 EC ?? 44 0F BF C1")]
        private readonly delegate* unmanaged<ushort, void> setTimeNative = null!;

        private delegate int OnCreateSceneDelegate(string territoryPath, uint p2, IntPtr p3, uint p4, IntPtr p5, int p6, uint p7);
        private delegate byte LobbyUpdateDelegate(GameLobbyType mapId, int time);
        private delegate ulong SelectCharacterDelegate(uint characterIndex, char unk);
        private delegate ulong SelectCharacter2Delegate(IntPtr self);
        private unsafe delegate void SetCameraCurveMidPointDelegate(LobbyCameraExpanded* self, float value);
        private delegate void SetCharSelectCurrentWorldDelegate(ulong unk);
        private delegate void SomeEnvManagerThingyDelegate(ulong unk1, uint unk2, float unk3);
        private delegate ulong WeatherThingyDelegate(ulong param, byte weatherId);
        private delegate void CharSelectSetWeatherDelegate();
        private delegate IntPtr PlayMusicDelegate(IntPtr self, string filename, float volume, uint fadeTime);

        private readonly Hook<OnCreateSceneDelegate> createSceneHook;
        private readonly Hook<LobbyUpdateDelegate> lobbyUpdateHook;
        private readonly Hook<SelectCharacterDelegate> selectCharacterHook;
        private readonly Hook<SelectCharacter2Delegate> selectCharacter2Hook;
        private readonly Hook<SetCameraCurveMidPointDelegate> setCameraCurveMidPointHook;
        private readonly Hook<SetCharSelectCurrentWorldDelegate> setCharSelectCurrentWorldHook;
        private readonly Hook<CharSelectSetWeatherDelegate> charSelectSetWeatherHook;
        private readonly Hook<PlayMusicDelegate> playMusicHook;

        private GameLobbyType lastLobbyUpdateMapId = GameLobbyType.Movie;
        private ulong lastContentId;
        private LocationModel locationModel = LocationService.DefaultLocation;

        private string? lastBgmPath;

        private bool resetScene = false;
        private bool resetCamera = false;

        private readonly IntPtr lobbyCurrentMapAddress;
        public short CurrentLobbyMap
        {
            get => Marshal.ReadInt16(lobbyCurrentMapAddress);
            set => Marshal.WriteInt16(lobbyCurrentMapAddress, value);
        }

        // Probably some lobby instance
        // method at E8 ?? ?? ?? ?? 33 C9 E8 ?? ?? ?? ?? 48 8B 0D picks a song from an array of 7 entries
        // ["", <arr title>, <char select>, <hw title>, <sb title>, <shb title>, <ew title>]
        // calls the method hooked at playMusicHook with selected path and stores the result at 0x18 with the index being stored at 0x20
        // on subsequent calls it checks if we need to reset by comparing offset 0x20 with provided music index
        // we abuse that by setting it back to 0
        private readonly IntPtr* lobbyBgmBasePointerAddress;
        public uint CurrentLobbyMusicIndex
        {
            get => (uint)Marshal.ReadInt32(*lobbyBgmBasePointerAddress, 0x20);
            set => Marshal.WriteInt32(*lobbyBgmBasePointerAddress, 0x20, (int)value);
        }

        public LobbyService()
        {
            Services.GameInteropProvider.InitializeFromAttributes(this);

            lobbyCurrentMapAddress = Services.SigScanner.GetStaticAddressFromSig("0F B7 05 ?? ?? ?? ?? 49 8B CE");
            lobbyBgmBasePointerAddress = (IntPtr*)Services.SigScanner.GetStaticAddressFromSig("48 8B 35 ?? ?? ?? ?? 88 46");

            createSceneHook = Services.GameInteropProvider.HookFromSignature<OnCreateSceneDelegate>("E8 ?? ?? ?? ?? 66 89 1D ?? ?? ?? ?? E9 ?? ?? ?? ??", OnCreateSceneDetour);
            lobbyUpdateHook = Services.GameInteropProvider.HookFromSignature<LobbyUpdateDelegate>("E8 ?? ?? ?? ?? EB 1C 3B CF", LobbyUpdateDetour);
            // Happends on character list hover - probably don't need to set player pos here cause covered by setCharSelectCurrentWorldHook
            selectCharacterHook = Services.GameInteropProvider.HookFromSignature<SelectCharacterDelegate>("E8 ?? ?? ?? ?? 0F B6 D8 84 C0 75 ?? 49 8B CD", SelectCharacterDetour);
            // Happens on world list hover
            selectCharacter2Hook = Services.GameInteropProvider.HookFromSignature<SelectCharacter2Delegate>("40 53 48 83 EC ?? 41 83 C8 ?? 4C 8D 15", SelectCharacter2Detour);
            setCameraCurveMidPointHook = Services.GameInteropProvider.HookFromSignature<SetCameraCurveMidPointDelegate>("0F 57 C0 0F 2F C1 73 ?? F3 0F 11 89", SetCameraCurveMidPointDetour);
            setCharSelectCurrentWorldHook = Services.GameInteropProvider.HookFromSignature<SetCharSelectCurrentWorldDelegate>("E8 ?? ?? ?? ?? 49 8B CD 48 8B 7C 24", SetCharSelectCurrentWorldDetour);

            // Some scene thingy
            charSelectSetWeatherHook = Services.GameInteropProvider.HookFromSignature<CharSelectSetWeatherDelegate>("0F B7 0D ?? ?? ?? ?? 8D 41", CharSelectSetWeatherDetour);

            playMusicHook = Services.GameInteropProvider.HookFromSignature<PlayMusicDelegate>("E8 ?? ?? ?? ?? 48 89 47 18 89 5F 20", PlayMusicDetour);

            Enable();

            Services.ClientState.Login += ResetState;
            Services.Framework.Update += Tick;
        }

        private unsafe void CharSelectSetWeatherDetour()
        {
            charSelectSetWeatherHook.Original();
            Services.Log.Debug($"CharSelectSetWeatherDetour {EnvManager.Instance()->ActiveWeather}");
            if (CurrentLobbyMap == (short)GameLobbyType.CharaSelect)
            {
                fixed (uint* pFestivals = locationModel.Festivals)
                {
                    Services.LayoutService.LayoutManager->layoutManager.SetActiveFestivals(pFestivals);
                }
                EnvManager.Instance()->ActiveWeather = locationModel.WeatherId;
                setTime(locationModel.TimeOffset);
                Services.Log.Debug($"SetWeather to {EnvManager.Instance()->ActiveWeather}");
                if (locationModel.Active != null && locationModel.Inactive != null)
                {
                    List<ulong> unknownUUIDs = new();
                    Services.LayoutService.ForEachInstance(instance =>
                    {
                        if (locationModel.Active.Contains(instance.Value->UUID))
                        {
                            SetActive(instance.Value, true);
                        }
                        else if (locationModel.Inactive.Contains(instance.Value->UUID))
                        {
                            SetActive(instance.Value, false);
                        }
                        else
                        {
                            unknownUUIDs.Add(instance.Value->UUID);
                        }
                    });
                    if (unknownUUIDs.Count > 0)
                    {
                        Services.Log.Debug($"{unknownUUIDs.Count} UUIDs not found in the layout data");
                    }
                }
                else
                {
                    Services.Log.Warning($"Layout data was null for {lastContentId:X16}");
                }

            }
        }
        private void SetActive(ILayoutInstance* instance, bool active)
        {
            if (instance->Id.Type == InstanceType.Vfx)
            {
                SetIndex((VfxLayoutInstance*)instance);
                instance->SetActiveVF54(active);
            }
            else
            {
                instance->SetActive(active);
            }
        }

        private void SetIndex(VfxLayoutInstance* instance)
        {
            if (locationModel.VfxTriggerIndexes.TryGetValue(instance->ILayoutInstance.UUID, out var index))
            {
                Services.LayoutService.SetVfxLayoutInstanceVfxTriggerIndex(instance, index);
            }
        }

        private IntPtr PlayMusicDetour(IntPtr self, string filename, float volume, uint fadeTime)
        {
            Services.Log.Debug($"PlayMusicDetour {self.ToInt64():X} {filename} {volume} {fadeTime}");

            if (CurrentLobbyMap == (short)GameLobbyType.CharaSelect && !locationModel.BgmPath.IsNullOrEmpty())
            {
                Services.Log.Debug($"Setting music to {locationModel.BgmPath}");
                filename = locationModel.BgmPath;
            }
            lastBgmPath = filename;
            return playMusicHook.Original(self, filename, volume, fadeTime);
        }

        private void SetCharSelectCurrentWorldDetour(ulong unk)
        {
            setCharSelectCurrentWorldHook.Original(unk);
            Services.Log.Debug("SetCharSelectCurrentWorldDetour");

            var charaSelectCharacterList = CharaSelectCharacterList.Instance();
            var clientObjectManager = ClientObjectManager.Instance();
            if (charaSelectCharacterList != null && clientObjectManager != null)
            {

                for (int i = 0; i < charaSelectCharacterList->CharacterMappingSpan.Length; i++)
                {
                    if (charaSelectCharacterList->CharacterMappingSpan[i].ContentId == 0)
                    {
                        break;
                    }
                    var contentId = charaSelectCharacterList->CharacterMappingSpan[i].ContentId;
                    var clientObjectIndex = charaSelectCharacterList->CharacterMappingSpan[i].ClientObjectIndex;
                    Services.Log.Debug($"{charaSelectCharacterList->CharacterMappingSpan[i].ContentId:X} to {charaSelectCharacterList->CharacterMappingSpan[i].ClientObjectIndex}");
                    var location = Services.LocationService.GetLocationModel(contentId);
                    var gameObject = clientObjectManager->GetObjectByIndex((ushort)clientObjectIndex);
                    if (gameObject != null)
                    {

                        gameObject->SetPosition(location.Position.X, location.Position.Y, location.Position.Z);

                        Services.Log.Debug($"{(IntPtr)gameObject:X} set to {location.Position} {location.Rotation}");
                    }
                    else
                    {
                        Services.Log.Debug("Gameobject was null?");
                    }
                }
                //Set current character cause SE forgot to do this ??
                *(CharaSelectCharacterList.StaticAddressPointers.ppGetCurrentCharacter) = GetCurrentCharacter();
                Services.Log.Debug($"Set current char to {(IntPtr)(*(CharaSelectCharacterList.StaticAddressPointers.ppGetCurrentCharacter)):X}");

            }
            else
            {
                Services.Log.Warning($"[SetCharSelectCurrentWorldDetour] failed to get instance {(IntPtr)charaSelectCharacterList:X} {(IntPtr)clientObjectManager:X}");
            }
        }

        public void Tick(IFramework framework)
        {
            if (lastContentId != 0 && CharaSelectCharacterList.GetCurrentCharacter() == null)
            {
                ResetState();
                resetScene = true;
            }
        }

        public unsafe void ResetState()
        {
            lastContentId = 0;
            locationModel = LocationService.DefaultLocation;
            resetCamera = true;
        }

        public void FixOn(LobbyCamera* camera, float[] cameraPos, float[] focusPos, float fovY)
        {
            if (fixOnNative == null)
                throw new InvalidOperationException("FixOn signature wasn't found!");

            fixOnNative(camera, cameraPos, focusPos, fovY);
        }

        public void setTime(ushort time)
        {
            if (setTimeNative == null)
                throw new InvalidOperationException("SetTime signature wasn't found!");

            setTimeNative(time);
        }

        private int OnCreateSceneDetour(string territoryPath, uint p2, IntPtr p3, uint p4, IntPtr p5, int p6, uint p7)
        {
            //Log($"HandleCreateScene {p1} {p2} {p3.ToInt64():X} {p4} {p5.ToInt64():X} {p6} {p7}");
            //_titleCameraNeedsSet = false;
            //_amForcingTime = false;
            //_amForcingWeather = false;
            Services.Log.Debug($"Loading Scene {lastLobbyUpdateMapId}");
            if (resetCamera)
            {
                var cameraManager = CameraManager.Instance();
                if (cameraManager != null)
                {
                    LobbyCameraExpanded* camera = (LobbyCameraExpanded*)cameraManager->LobbCamera;
                    camera->lowPoint.value = 1.4350828f;
                    camera->midPoint.value = 0.85870504f;
                    camera->highPoint.value = 0.6742642f;
                    camera->LobbyCamera.Camera.CameraBase.SceneCamera.LookAtVector = FFXIVClientStructs.FFXIV.Common.Math.Vector3.Zero;
                }

                Services.Log.Debug($"Reset Lobby camera");
                resetCamera = false;
            }
            if (lastLobbyUpdateMapId == GameLobbyType.CharaSelect)
            {
                //RefreshCurrentTitleEditScreen();

                territoryPath = locationModel.TerritoryPath;
                Services.Log.Debug($"Loading char select screen: {territoryPath}");
                var returnVal = createSceneHook.Original(territoryPath, p2, p3, p4, p5, p6, p7);
                if ((!locationModel.BgmPath.IsNullOrEmpty() && lastBgmPath != locationModel.BgmPath) || (locationModel.BgmPath.IsNullOrEmpty() && lastBgmPath != LocationService.DefaultLocation.BgmPath))
                {
                    CurrentLobbyMusicIndex = 0;
                }
                //SetWeather();
                //var camera = CameraManager.Instance()->LobbCamera;
                //if (lastContentId == 0 && camera != null)
                //{
                //    FixOn(camera, Vector3.Zero.ToArray(), new Vector3(0, 0.8580103f, 0).ToArray(), 1);
                //}
                //ForceWeather(_currentScreen.WeatherId, 5000);
                //ForceTime(_currentScreen.TimeOffset, 5000);
                //FixOn(_currentScreen.CameraPos, _currentScreen.FixOnPos, 1);
                // SetRevisionStringVisibility(_configuration.DisplayVersionText);
                return returnVal;
            }
            return createSceneHook.Original(territoryPath, p2, p3, p4, p5, p6, p7);
        }

        private byte LobbyUpdateDetour(GameLobbyType mapId, int time)
        {
            lastLobbyUpdateMapId = mapId;
            Services.Log.Verbose($"mapId {mapId}");
            if (resetScene)
            {
                resetScene = false;
                CurrentLobbyMap = (short)GameLobbyType.Movie;
            }

            return lobbyUpdateHook.Original(mapId, time);
        }

        //SE's implenetation does nothing if value is below 0 which breaks the camera when character is in negative Y
        private unsafe void SetCameraCurveMidPointDetour(LobbyCameraExpanded* self, float value)
        {
            self->midPoint.value = value;
        }

        private ulong SelectCharacter2Detour(IntPtr self)
        {
            Services.Log.Debug($"SelectCharacter2Detour");
            var result = selectCharacter2Hook.Original(self);
            UpdateCharacter();
            return result;
        }

        private ulong SelectCharacterDetour(uint characterIndex, char unk)
        {
            Services.Log.Debug($"SelectCharacterDetour");
            var result = selectCharacterHook.Original(characterIndex, unk);
            Services.Log.Debug($"{result}");
            UpdateCharacter();
            return result;
        }

        private unsafe FFXIVClientStructs.FFXIV.Client.Game.Character.Character* GetCurrentCharacter()
        {

            var agentLobby = AgentLobby.Instance();
            var charaSelectCharacterList = CharaSelectCharacterList.Instance();
            var clientObjectManager = ClientObjectManager.Instance();
            if (agentLobby != null && charaSelectCharacterList != null && clientObjectManager != null)
            {
                if (agentLobby->HoveredCharacterIndex == -1)
                {
                    return null;
                }
                var clientObjectIndex = charaSelectCharacterList->CharacterMappingSpan[agentLobby->HoveredCharacterIndex].ClientObjectIndex;
                if (clientObjectIndex == -1)
                {
                    Services.Log.Warning($"[getCurrentCharacter] clientObjectIndex -1 for {agentLobby->HoveredCharacterIndex}");
                    return null;
                }
                return (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)clientObjectManager->GetObjectByIndex((ushort)clientObjectIndex);
            }
            else
            {
                Services.Log.Warning($"[getCurrentCharacter] failed to get instance  {(IntPtr)agentLobby:X} {(IntPtr)charaSelectCharacterList:X} {(IntPtr)clientObjectManager:X}");

            }
            return null;
        }

        private unsafe void UpdateCharacter()
        {
            var character = CharaSelectCharacterList.GetCurrentCharacter();

            if (character != null)
            {
                var agentLobby = AgentLobby.Instance();
                if (agentLobby != null)
                {
                    var contentId = agentLobby->LobbyData.CharaSelectEntries.Get((ulong)agentLobby->HoveredCharacterIndex).Value->ContentId;
                    if (lastContentId != contentId)
                    {
                        lastContentId = contentId;

                        var newLocationModel = Services.LocationService.GetLocationModel(contentId);
                        if (!newLocationModel.Equals(locationModel))
                        {
                            locationModel = newLocationModel;
                            resetScene = true;
                        }
                        Services.Log.Debug($"Setting character postion {(IntPtr)character:X}");
                        character->GameObject.SetPosition(locationModel.Position.X, locationModel.Position.Y, locationModel.Position.Z);

                    }
                }


            }
            else
            {
                Services.Log.Info("Character was null :(");
            }

        }
        public void Enable()
        {
            createSceneHook.Enable();
            lobbyUpdateHook.Enable();
            selectCharacterHook.Enable();
            selectCharacter2Hook.Enable();
            setCameraCurveMidPointHook.Enable();
            setCharSelectCurrentWorldHook.Enable();
            charSelectSetWeatherHook.Enable();
            playMusicHook.Enable();
        }

        public void Dispose()
        {
            createSceneHook?.Dispose();
            lobbyUpdateHook?.Dispose();
            selectCharacterHook?.Dispose();
            selectCharacter2Hook?.Dispose();
            setCameraCurveMidPointHook?.Dispose();
            setCharSelectCurrentWorldHook?.Dispose();
            charSelectSetWeatherHook?.Dispose();
            playMusicHook?.Dispose();
            Services.ClientState.Login -= ResetState;
        }
    }
}
