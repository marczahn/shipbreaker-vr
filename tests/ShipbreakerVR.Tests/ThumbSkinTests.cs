using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    // The right hand's skin takes the left hand's share for the thumb's base bone (VrThumbSkin.Bind).
    public class ThumbSkinTests
    {
        private const int LeftBase = 1, RightBase = 2, RightHand = 3, Other = 4, RightThumb = 5;

        private static BoneWeight Weights(int a, float wa, int b = 0, float wb = 0f, int c = 0, float wc = 0f, int d = 0, float wd = 0f) =>
            new BoneWeight { boneIndex0 = a, weight0 = wa, boneIndex1 = b, weight1 = wb, boneIndex2 = c, weight2 = wc, boneIndex3 = d, weight3 = wd };

        private static float On(BoneWeight w, int bone) =>
            (w.boneIndex0 == bone ? w.weight0 : 0f) + (w.boneIndex1 == bone ? w.weight1 : 0f)
            + (w.boneIndex2 == bone ? w.weight2 : 0f) + (w.boneIndex3 == bone ? w.weight3 : 0f);

        [Fact]
        public void TheMirrorImageTakesTheShare()
        {
            var vertices = new[] { new Vector3(-0.5f, 1f, 0.2f), new Vector3(0.5f, 1f, 0.2f) };
            var weights = new[] { Weights(LeftBase, 0.6f, Other, 0.4f), Weights(RightHand, 0.7f, RightThumb, 0.3f) };
            var changed = VrThumbSkin.Bind(vertices, weights, LeftBase, RightBase, RightHand, out var moved);
            Assert.Equal(1, changed);
            Assert.Equal(0.6f, moved, 4);
            Assert.Equal(0.6f, On(weights[1], RightBase), 4);
            Assert.Equal(0.1f, On(weights[1], RightHand), 4);
            Assert.Equal(0.3f, On(weights[1], RightThumb), 4);
            Assert.Equal(0.6f, On(weights[0], LeftBase), 4);
        }

        // No more than the hand bone held: the rest of the vertex stays as it was.
        [Fact]
        public void NoMoreThanTheHandBoneHeld()
        {
            var vertices = new[] { new Vector3(-0.5f, 1f, 0.2f), new Vector3(0.5f, 1f, 0.2f) };
            var weights = new[] { Weights(LeftBase, 0.8f, Other, 0.2f), Weights(RightThumb, 0.7f, RightHand, 0.3f) };
            VrThumbSkin.Bind(vertices, weights, LeftBase, RightBase, RightHand, out var moved);
            Assert.Equal(0.3f, moved, 4);
            Assert.Equal(0.3f, On(weights[1], RightBase), 4);
            Assert.Equal(0f, On(weights[1], RightHand), 4);
            Assert.Equal(0.7f, On(weights[1], RightThumb), 4);
        }

        // Vertices at one place (split along a UV seam) stay together.
        [Fact]
        public void VerticesAtOnePlaceChangeAlike()
        {
            var vertices = new[] { new Vector3(-0.5f, 1f, 0.2f), new Vector3(0.5f, 1f, 0.2f), new Vector3(0.5f, 1f, 0.2f), new Vector3(0.5f, 1.2f, 0.2f) };
            var weights = new[] { Weights(LeftBase, 0.5f, Other, 0.5f), Weights(RightHand, 1f), Weights(RightHand, 1f), Weights(RightHand, 1f) };
            var changed = VrThumbSkin.Bind(vertices, weights, LeftBase, RightBase, RightHand, out _);
            Assert.Equal(2, changed);
            Assert.Equal(0.5f, On(weights[1], RightBase), 4);
            Assert.Equal(0.5f, On(weights[2], RightBase), 4);
            Assert.Equal(0f, On(weights[3], RightBase), 4);        // no mirror image on the left: untouched
        }

        // A mesh whose right base bone carries weight already is left alone (a copy of a copy, or a mesh bound alike).
        [Fact]
        public void BoundAlreadyStaysAsItIs()
        {
            var vertices = new[] { new Vector3(-0.5f, 1f, 0.2f), new Vector3(0.5f, 1f, 0.2f) };
            var weights = new[] { Weights(LeftBase, 0.6f, Other, 0.4f), Weights(RightHand, 1f) };
            Assert.Equal(1, VrThumbSkin.Bind(vertices, weights, LeftBase, RightBase, RightHand, out _));
            Assert.Equal(0, VrThumbSkin.Bind(vertices, weights, LeftBase, RightBase, RightHand, out var again));
            Assert.Equal(0f, again);
            Assert.Equal(0.6f, On(weights[1], RightBase), 4);
        }

        // Five bones on a vertex: the smallest share goes, the rest sums to one again.
        [Fact]
        public void TheSmallestShareGoesWhenFiveBonesMeet()
        {
            var vertices = new[] { new Vector3(-0.5f, 1f, 0.2f), new Vector3(0.5f, 1f, 0.2f) };
            var weights = new[] { Weights(LeftBase, 0.2f, Other, 0.8f), Weights(RightHand, 0.5f, RightThumb, 0.3f, Other, 0.15f, 6, 0.05f) };
            VrThumbSkin.Bind(vertices, weights, LeftBase, RightBase, RightHand, out _);
            var w = weights[1];
            Assert.Equal(1f, w.weight0 + w.weight1 + w.weight2 + w.weight3, 4);
            Assert.Equal(0f, On(w, 6), 4);
            Assert.True(On(w, RightBase) > 0.2f);
            Assert.True(w.weight0 >= w.weight1 && w.weight1 >= w.weight2 && w.weight2 >= w.weight3);
        }
    }
}
