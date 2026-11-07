using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using Xunit;

namespace Optimum.Tests;

public sealed class ShapeJointMemoizationTests
{
    [Fact]
    public void ShapePatch_MemoizesJointResolution()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VintagestoryApi/Common/Model/Shape/Shape.cs.patch");
        Assert.True(File.Exists(patchPath), "Shape.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("JointsResolved", patched);
        Assert.Contains("lock (jointsLock)", patched);
        Assert.Contains("resolvedRequiredJoints", patched);
    }

    [Fact]
    public void Shape_ResolveAndFindJoints_SetsJointsResolved()
    {
        var shape = new Shape
        {
            Elements = new ShapeElement[]
            {
                new ShapeElement { Name = "body" },
                new ShapeElement { Name = "head" }
            },
            Animations = new Animation[]
            {
                new Animation
                {
                    Code = "walk",
                    Version = 1,
                    KeyFrames = new AnimationKeyFrame[]
                    {
                        new AnimationKeyFrame
                        {
                            Elements = new Dictionary<string, AnimationKeyFrameElement>
                            {
                                ["head"] = new AnimationKeyFrameElement()
                            }
                        }
                    }
                }
            }
        };

        Assert.False(shape.JointsResolved);
        shape.ResolveAndFindJoints(null!, "testShape");
        Assert.True(shape.JointsResolved);
        Assert.NotEmpty(shape.JointsById);

        int countBefore = shape.JointsById.Count;

        // Calling again should safely early-exit without modifying joints
        shape.ResolveAndFindJoints(null!, "testShape");
        Assert.True(shape.JointsResolved);
        Assert.Equal(countBefore, shape.JointsById.Count);
    }
}
