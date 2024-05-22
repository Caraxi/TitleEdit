using System.Numerics;

namespace CharacterSelectBackgroundPlugin.Data
{
    public struct LocationModel
    {
        public string TerritoryPath;
        public Vector3 Position;
        public float Rotation;
        public byte WeatherId;
        public ushort TimeOffset;
        public string BgmPath;
    }
}
