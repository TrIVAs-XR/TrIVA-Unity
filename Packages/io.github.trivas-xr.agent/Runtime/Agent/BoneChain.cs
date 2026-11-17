using System.Collections.Generic;
using UnityEngine;

namespace TrIVA.Agent
{
    /// <summary>
    /// A chain of humanoid bones that share one turn between them, such as spine and chest for a torso turn or chest, neck and head for a gaze turn.
    /// </summary>
    internal class BoneChain
    {
        private readonly Transform[] _bones;
        private readonly float[] _shares;

        /// <summary>
        /// Collects the bones the rig has, each with its share of the turn, rescaled so the shares of the bones found add up to one.
        /// </summary>
        public BoneChain(Animator animator, HumanBodyBones[] bones, float[] shares)
        {
            List<Transform> found = new();
            List<float> foundShares = new();
            float total = 0f;
            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = animator.GetBoneTransform(bones[i]);
                if (!bone)
                    continue;
                found.Add(bone);
                foundShares.Add(shares[i]);
                total += shares[i];
            }

            _bones = found.ToArray();
            _shares = foundShares.ConvertAll(share => share / total).ToArray();
        }

        /// <summary>
        /// Turns the chain on top of its current pose by a pitch (positive down) and yaw (positive right) in degrees around the axes of the given space, each bone taking its share.
        /// </summary>
        public void Turn(Quaternion space, float pitch, float yaw)
        {
            Quaternion toSpace = Quaternion.Inverse(space);
            for (int i = 0; i < _bones.Length; i++)
            {
                Quaternion turn = space * Quaternion.Euler(pitch * _shares[i], yaw * _shares[i], 0f) * toSpace;
                _bones[i].rotation = turn * _bones[i].rotation;
            }
        }
    }
}
