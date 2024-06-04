using CharacterSelectBackgroundPlugin.Data.Character;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CharacterSelectBackgroundPlugin.Data.Persistence
{
    public struct LocationModel
    {
        public int Version = 1;
        public string TerritoryPath = "";
        public ushort TerritoryTypeId;
        public Vector3 Position;
        public float Rotation;
        public byte WeatherId;
        public ushort TimeOffset;
        public uint BgmId = 0;
        public string? BgmPath;
        public MovementMode MovementMode = MovementMode.Normal;
        public MountModel Mount;
        public HashSet<ulong> Active = [];
        public HashSet<ulong> Inactive = [];
        public Dictionary<ulong, short> VfxTriggerIndexes = [];
        public uint[] Festivals = new uint[4];
        // Only set when used with a preset
        [NonSerialized]
        public CameraFollowMode CameraFollowMode = CameraFollowMode.Inherit;

        public LocationModel()
        {
        }
    }

    public struct MountModel
    {
        public uint MountId = 0;
        public uint BuddyModelTop = 0;
        public uint BuddyModelBody = 0;
        public uint BuddyModelLegs = 0;
        public byte BuddyStain = 0;

        public MountModel()
        {
        }
    }
}
