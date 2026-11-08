using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Keeps a player's name label readable: upright and parked just below the
    /// body whatever way the body is turned. The label is a child of the body (so
    /// it is created, hidden and destroyed with it) but its world rotation and
    /// offset are reset after the body has moved each frame.
    ///
    /// Allocation-free: two transform writes per player per frame.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_NameTag : MonoBehaviour
    {
        private Transform _body;
        private float _below;

        /// <summary>Called by Agent_SoccerView when it builds the tag.</summary>
        public void Bind(Transform body, float worldDistanceBelow)
        {
            _body = body;
            _below = worldDistanceBelow;
            LateUpdate();
        }

        private void LateUpdate()
        {
            if (_body == null)
            {
                return;
            }
            transform.SetPositionAndRotation(_body.position + new Vector3(0f, -_below, 0f), Quaternion.identity);
        }
    }
}
