using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

#nullable disable

namespace Vintagestory.GameContent
{
    internal static class SelectionBoxRayBounds
    {
        // SelectionBox is centered on the entity origin. Its maximum L1 corner radius
        // conservatively encloses the box without a square root. The two-block attachment
        // margin follows the issue's proposed reach bound.
        internal static bool IsEntityBeyondRayRange(
            double rayOriginX, double rayOriginY, double rayOriginZ,
            double entityX, double entityY, double entityZ,
            Cuboidf selectionBox, double rayLength)
        {
            if (selectionBox == null || !double.IsFinite(rayOriginX) || !double.IsFinite(rayOriginY) || !double.IsFinite(rayOriginZ) ||
                !double.IsFinite(entityX) || !double.IsFinite(entityY) || !double.IsFinite(entityZ) ||
                !double.IsFinite(rayLength) || rayLength < 0 ||
                !float.IsFinite(selectionBox.X1) || !float.IsFinite(selectionBox.X2) ||
                !float.IsFinite(selectionBox.Y1) || !float.IsFinite(selectionBox.Y2) ||
                !float.IsFinite(selectionBox.Z1) || !float.IsFinite(selectionBox.Z2))
            {
                return false;
            }

            double radiusX = Math.Max(Math.Abs((double)selectionBox.X1), Math.Abs((double)selectionBox.X2));
            double radiusY = Math.Max(Math.Abs((double)selectionBox.Y1), Math.Abs((double)selectionBox.Y2));
            double radiusZ = Math.Max(Math.Abs((double)selectionBox.Z1), Math.Abs((double)selectionBox.Z2));
            double reach = rayLength + radiusX + radiusY + radiusZ + 2;
            double dx = rayOriginX - entityX;
            double dy = rayOriginY - entityY;
            double dz = rayOriginZ - entityZ;
            double distanceSq = dx * dx + dy * dy + dz * dz;
            double reachSq = reach * reach;

            if (!double.IsFinite(distanceSq) || !double.IsFinite(reachSq)) return false;

            double comparisonMargin = Math.Max(1, Math.Max(distanceSq, reachSq)) * 1e-12;
            return distanceSq > reachSq + comparisonMargin;
        }

        // The existing narrow phase accepts a transformed hit when its Euclidean distance
        // from the ray origin is within the ray length. This AABB only rejects boxes whose
        // transformed unit cube is conservatively farther away than that same distance.
        internal static bool IsBeyondRayLength(Matrixf transform, Vec3d rayOrigin, double rayLengthSq)
        {
            if (transform?.Values == null || transform.Values.Length < 16 || rayOrigin == null ||
                !double.IsFinite(rayLengthSq) || rayLengthSq < 0 ||
                !double.IsFinite(rayOrigin.X) || !double.IsFinite(rayOrigin.Y) || !double.IsFinite(rayOrigin.Z))
            {
                return false;
            }

            float[] m = transform.Values;

            // applyBoxTransform produces affine matrices. Fail open for any other matrix so
            // unusual or mod-provided transforms keep the original intersection path.
            if (!double.IsFinite(m[3]) || !double.IsFinite(m[7]) || !double.IsFinite(m[11]) || !double.IsFinite(m[15]) ||
                m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1)
            {
                return false;
            }

            double minX = m[12] + Math.Min(0, m[0]) + Math.Min(0, m[4]) + Math.Min(0, m[8]);
            double maxX = m[12] + Math.Max(0, m[0]) + Math.Max(0, m[4]) + Math.Max(0, m[8]);
            double minY = m[13] + Math.Min(0, m[1]) + Math.Min(0, m[5]) + Math.Min(0, m[9]);
            double maxY = m[13] + Math.Max(0, m[1]) + Math.Max(0, m[5]) + Math.Max(0, m[9]);
            double minZ = m[14] + Math.Min(0, m[2]) + Math.Min(0, m[6]) + Math.Min(0, m[10]);
            double maxZ = m[14] + Math.Max(0, m[2]) + Math.Max(0, m[6]) + Math.Max(0, m[10]);

            if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY) ||
                !double.IsFinite(minZ) || !double.IsFinite(maxZ))
            {
                return false;
            }

            // Leave room for floating-point error in both the transformed hit and this
            // lower-bound calculation. The margin scales with the coordinates involved.
            double scale = Math.Max(1, Math.Max(
                Math.Max(Math.Abs(rayOrigin.X), Math.Max(Math.Abs(rayOrigin.Y), Math.Abs(rayOrigin.Z))),
                Math.Max(Math.Max(Math.Abs(minX), Math.Abs(maxX)),
                    Math.Max(Math.Max(Math.Abs(minY), Math.Abs(maxY)), Math.Max(Math.Abs(minZ), Math.Abs(maxZ))))));
            double margin = scale * 1e-12;

            minX -= margin;
            maxX += margin;
            minY -= margin;
            maxY += margin;
            minZ -= margin;
            maxZ += margin;

            double dx = rayOrigin.X < minX ? minX - rayOrigin.X : rayOrigin.X > maxX ? rayOrigin.X - maxX : 0;
            double dy = rayOrigin.Y < minY ? minY - rayOrigin.Y : rayOrigin.Y > maxY ? rayOrigin.Y - maxY : 0;
            double dz = rayOrigin.Z < minZ ? minZ - rayOrigin.Z : rayOrigin.Z > maxZ ? rayOrigin.Z - maxZ : 0;
            double distanceSq = dx * dx + dy * dy + dz * dz;

            if (double.IsPositiveInfinity(distanceSq)) return true;
            if (!double.IsFinite(distanceSq)) return false;

            double comparisonMargin = Math.Max(1, Math.Max(distanceSq, rayLengthSq)) * 1e-12;
            return distanceSq > rayLengthSq + comparisonMargin;
        }
    }
}
