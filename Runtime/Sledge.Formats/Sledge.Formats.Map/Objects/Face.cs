using System.Collections.Generic;
using System.Numerics;

namespace Sledge.Formats.Map.Objects
{
    public class Face : Surface
    {
        public Plane Plane { get; set; }
        public List<Vector3> Vertices { get; set; } // why are we using system numerics?

        public Face()
        {
            Vertices = new List<Vector3>();
        }
    }
}