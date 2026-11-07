using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// What a brain is actually reading: occlusion saliency over its input groups.
    ///
    /// METHOD. Take the observation the brain was just given, run its own network on
    /// it once for a baseline action, then once more per input group with that group
    /// blanked. The mean absolute change in the four action channels is how much the
    /// decision leaned on that group. Ten groups, eleven forward passes of a 256x2
    /// MLP on the CPU - well under a millisecond, and only for one player, only while
    /// the brain cam is open, at a throttled rate.
    ///
    /// "Blank" means the input says nothing: zero for a vector feature, and for a ray
    /// the encoding of a clean miss (no tag, miss flag 1, distance 1). Zero is not
    /// neutral for a ray - a zero miss flag and zero distance reads as "something is
    /// touching my face" - so blanking rays with zeros would measure the brain's
    /// reaction to a phantom, not its reliance on the sensor.
    ///
    /// THIS NEVER TOUCHES THE AGENT'S REAL DECISION. It owns a separate Worker on a
    /// separate copy of the model and only reads the agent's sensors after they have
    /// been written. The policy that drives the body is ML-Agents' own ModelRunner,
    /// untouched.
    ///
    /// INPUT ORDER. ML-Agents feeds the network one tensor per sensor, named obs_0..n
    /// in the order of the sensors sorted by name (SensorUtils.SortSensors). That
    /// order is reproduced here from the same names, and every slot's size is checked
    /// against the model before anything runs; on any mismatch the class reports why
    /// and stays idle rather than guessing.
    ///
    /// One caveat stated rather than hidden: occlusion measures sensitivity around
    /// THIS observation. A group the brain relies on only when it changes (an
    /// opponent appearing) can read low while nothing is happening.
    /// </summary>
    public sealed class Agent_Saliency : IDisposable
    {
        /// <summary>Input groups, in display order.</summary>
        public enum Group
        {
            BallRays = 0,
            OpponentRays = 1,
            WallRays = 2,
            Self = 3,
            Ball = 4,
            Goals = 5,
            Teammate = 6,
            Opponents = 7,
            Role = 8,
            Clock = 9,
        }

        public const int GROUP_COUNT = 10;

        public static readonly string[] GroupNames =
        {
            "BALL RAYS", "OPP RAYS", "WALL RAYS", "SELF", "BALL", "GOALS", "MATE", "OPPONENTS", "ROLE", "CLOCK",
        };

        const string DETERMINISTIC_OUTPUT = "deterministic_continuous_actions";
        const string STOCHASTIC_OUTPUT = "continuous_actions";
        const string OBSERVATION_PREFIX = "obs_";

        sealed class Slot
        {
            public string InputName;
            public string SensorName;
            public RayPerceptionSensorComponent2D Ray;   // null for the vector slot
            public Group RayGroup;
            public int Size;
            public float[] Data;
            public Tensor<float> Tensor;
        }

        readonly List<Slot> _slots = new();
        readonly float[] _baseline = new float[Agent_Soccer.ContinuousActionCount];
        readonly float[] _occluded = new float[Agent_Soccer.ContinuousActionCount];
        readonly float[] _raw = new float[GROUP_COUNT];
        readonly float[] _shares = new float[GROUP_COUNT];

        Worker _worker;
        string _outputName;
        int _vectorBaseSize;
        int _vectorStacks = 1;

        /// <summary>The agent this instance is bound to, or null.</summary>
        public Agent_Soccer Subject { get; private set; }

        /// <summary>The model it was bound against, to detect a brain swap.</summary>
        public ModelAsset BoundModel { get; private set; }

        /// <summary>True once bound and the input layout checked out.</summary>
        public bool Ready { get; private set; }

        /// <summary>Why it is not ready, or the last failure. Empty when fine.</summary>
        public string Status { get; private set; } = "";

        /// <summary>Largest raw action change seen in the last computation.</summary>
        public float Peak { get; private set; }

        /// <summary>Share of total sensitivity for one group, 0..1, summing to 1 across groups.</summary>
        public float ShareOf(int group) => group >= 0 && group < GROUP_COUNT ? _shares[group] : 0f;

        // -- Pure mapping, pinned by the EditMode tests -----------------------

        /// <summary>
        /// Group of one feature of the 34-float base observation, in the order
        /// Agent_Soccer.CollectObservations writes them: wall-kick and clock (3),
        /// self (5: velocity 2, yaw rate, ball bearing, stamina), ball (4), goals (5),
        /// teammate (4), opponents (4 x OpponentSlots), role (5).
        /// </summary>
        public static Group VectorGroupOf(int baseIndex)
        {
            if (baseIndex < 3) return Group.Clock;
            if (baseIndex < 8) return Group.Self;
            if (baseIndex < 12) return Group.Ball;
            if (baseIndex < 17) return Group.Goals;
            if (baseIndex < 21) return Group.Teammate;
            if (baseIndex < 21 + 4 * Agent_Soccer.OpponentSlots) return Group.Opponents;
            return Group.Role;
        }

        /// <summary>Group of a ray sensor by its Sensor_Vision name; false for a sensor this class does not know.</summary>
        public static bool TryRayGroupOf(string sensorName, out Group group)
        {
            switch (sensorName)
            {
                case "Sensor_Ball": group = Group.BallRays; return true;
                case "Sensor_Opponents": group = Group.OpponentRays; return true;
                case "Sensor_Walls": group = Group.WallRays; return true;
                default: group = Group.Clock; return false;
            }
        }

        /// <summary>
        /// The blanked value of feature <paramref name="index"/> of a ray block with
        /// <paramref name="detectableTags"/> tags: no tag hit, miss flag 1, distance 1.
        /// </summary>
        public static float NeutralRayValue(int index, int detectableTags)
        {
            int stride = detectableTags + 2;
            int within = index % stride;
            return within >= detectableTags ? 1f : 0f;
        }

        /// <summary>
        /// Normalise raw sensitivities into shares that sum to 1. All-zero input (a
        /// brain whose output did not move at all) yields all-zero shares rather than
        /// a division by zero.
        /// </summary>
        public static void Normalise(float[] raw, float[] shares)
        {
            float total = 0f;
            for (int groupIndex = 0; groupIndex < raw.Length; groupIndex++) total += Mathf.Max(0f, raw[groupIndex]);
            for (int groupIndex = 0; groupIndex < raw.Length && groupIndex < shares.Length; groupIndex++)
            {
                shares[groupIndex] = total > 1e-7f ? Mathf.Max(0f, raw[groupIndex]) / total : 0f;
            }
        }

        // -- Binding ---------------------------------------------------------

        /// <summary>
        /// Bind to an agent's current brain. Returns false, with <see cref="Status"/>
        /// set, when the agent has no model or its input layout is not one this class
        /// can reproduce.
        /// </summary>
        public bool Bind(Agent_Soccer agent)
        {
            Release();
            Subject = agent;
            if (agent == null) { Status = "no player"; return false; }
            if (agent.RuleBased) { Status = "rule-based: no network to read"; return false; }

            var behavior = agent.GetComponent<BehaviorParameters>();
            if (behavior == null || behavior.Model == null) { Status = "no brain assigned"; return false; }
            BoundModel = behavior.Model;

            Model model;
            try
            {
                model = ModelLoader.Load(BoundModel);
            }
            catch (Exception exception)
            {
                Status = "model failed to load: " + exception.Message;
                return false;
            }

            _outputName = null;
            for (int outputIndex = 0; outputIndex < model.outputs.Count; outputIndex++)
            {
                // Contains, not equality: that is how ML-Agents' own SentisModelInfo
                // matches these names, and an exporter can decorate them.
                string name = model.outputs[outputIndex].name;
                if (name.Contains(DETERMINISTIC_OUTPUT)) { _outputName = name; break; }
                if (name.Contains(STOCHASTIC_OUTPUT)) _outputName = name;
            }
            if (_outputName == null) { Status = "model has no continuous action output"; return false; }

            // Reproduce ML-Agents' sensor list and its name sort.
            var brain = behavior.BrainParameters;
            _vectorBaseSize = brain.VectorObservationSize;
            int stacks = Mathf.Max(1, brain.NumStackedVectorObservations);
            _vectorStacks = stacks;
            var entries = new List<Slot>();
            if (_vectorBaseSize > 0)
            {
                string vectorName = $"VectorSensor_size{_vectorBaseSize}";
                if (stacks > 1) vectorName = $"StackingSensor_size{stacks}_{vectorName}";
                entries.Add(new Slot { SensorName = vectorName, Size = _vectorBaseSize * stacks });
            }

            var rays = agent.GetComponents<RayPerceptionSensorComponent2D>();
            for (int rayIndex = 0; rayIndex < rays.Length; rayIndex++)
            {
                var ray = rays[rayIndex];
                if (ray.ObservationStacks > 1) { Status = "stacked ray sensors are not supported"; return false; }
                if (!TryRayGroupOf(ray.SensorName, out var group))
                {
                    Status = "unknown ray sensor " + ray.SensorName;
                    return false;
                }
                entries.Add(new Slot { SensorName = ray.SensorName, Ray = ray, RayGroup = group });
            }

            entries.Sort((left, right) => string.Compare(left.SensorName, right.SensorName,
                StringComparison.InvariantCulture));

            // Every model input must be one of our slots, at the size we will fill.
            if (model.inputs.Count != entries.Count)
            {
                Status = $"model takes {model.inputs.Count} inputs, agent has {entries.Count} sensors";
                return false;
            }

            for (int slotIndex = 0; slotIndex < entries.Count; slotIndex++)
            {
                var slot = entries[slotIndex];
                slot.InputName = OBSERVATION_PREFIX + slotIndex;

                int features = -1;
                for (int inputIndex = 0; inputIndex < model.inputs.Count; inputIndex++)
                {
                    if (model.inputs[inputIndex].name != slot.InputName) continue;
                    features = model.inputs[inputIndex].shape.Get(-1);
                    break;
                }
                if (features <= 0)
                {
                    Status = $"model has no input {slot.InputName}";
                    return false;
                }
                if (slot.Ray == null && features != slot.Size)
                {
                    Status = $"{slot.InputName}: model wants {features}, vector gives {slot.Size}";
                    return false;
                }

                // Ray sizes are checked against the live output on every fill.
                slot.Size = features;
                slot.Data = new float[features];
                slot.Tensor = new Tensor<float>(new TensorShape(1, features));
            }

            _slots.AddRange(entries);
            _worker = new Worker(model, BackendType.CPU);
            Ready = true;
            Status = "";
            return true;
        }

        // -- Computation -----------------------------------------------------

        /// <summary>
        /// Recompute the shares from the subject's current observation. Returns false,
        /// with <see cref="Status"/> set, if the observation could not be read.
        /// Allocates one small list (ML-Agents' GetStackedObservations), so call it at
        /// a throttled rate, never per frame.
        /// </summary>
        public bool Compute()
        {
            if (!Ready || Subject == null || _worker == null) return false;
            if (!ReadObservation()) return false;

            Run(-1, _baseline);
            float peak = 0f;
            for (int groupIndex = 0; groupIndex < GROUP_COUNT; groupIndex++)
            {
                Run(groupIndex, _occluded);
                float delta = 0f;
                for (int channel = 0; channel < _baseline.Length; channel++)
                {
                    delta += Mathf.Abs(_occluded[channel] - _baseline[channel]);
                }
                delta /= _baseline.Length;
                _raw[groupIndex] = delta;
                if (delta > peak) peak = delta;
            }

            Peak = peak;
            Normalise(_raw, _shares);
            return true;
        }

        bool ReadObservation()
        {
            for (int slotIndex = 0; slotIndex < _slots.Count; slotIndex++)
            {
                var slot = _slots[slotIndex];
                if (slot.Ray == null)
                {
                    // GetStackedObservations dereferences a stacking sensor that only
                    // exists when stacks > 1; an unstacked brain reads the plain one.
                    var stacked = _vectorStacks > 1
                        ? Subject.GetStackedObservations()
                        : Subject.GetObservations();
                    if (stacked == null || stacked.Count != slot.Size)
                    {
                        Status = "vector observation not ready";
                        return false;
                    }
                    for (int featureIndex = 0; featureIndex < slot.Size; featureIndex++)
                    {
                        slot.Data[featureIndex] = stacked[featureIndex];
                    }
                    continue;
                }

                var sensor = slot.Ray.RaySensor;
                var outputs = sensor != null && sensor.RayPerceptionOutput != null
                    ? sensor.RayPerceptionOutput.RayOutputs
                    : null;
                int tags = slot.Ray.DetectableTags != null ? slot.Ray.DetectableTags.Count : 0;
                if (outputs == null || outputs.Length * (tags + 2) != slot.Size)
                {
                    Status = $"{slot.SensorName} not ready";
                    return false;
                }

                Array.Clear(slot.Data, 0, slot.Data.Length);
                for (int rayIndex = 0; rayIndex < outputs.Length; rayIndex++)
                {
                    outputs[rayIndex].ToFloatArray(tags, rayIndex, slot.Data);
                }
            }

            Status = "";
            return true;
        }

        /// <summary>One forward pass with <paramref name="occludedGroup"/> blanked (-1 for none).</summary>
        void Run(int occludedGroup, float[] actions)
        {
            for (int slotIndex = 0; slotIndex < _slots.Count; slotIndex++)
            {
                var slot = _slots[slotIndex];
                var tensor = slot.Tensor;
                tensor.CompleteAllPendingOperations();

                int tags = slot.Ray != null && slot.Ray.DetectableTags != null ? slot.Ray.DetectableTags.Count : 0;
                bool rayBlanked = slot.Ray != null && (int)slot.RayGroup == occludedGroup;

                for (int featureIndex = 0; featureIndex < slot.Size; featureIndex++)
                {
                    float value = slot.Data[featureIndex];
                    if (slot.Ray == null)
                    {
                        if (occludedGroup >= 0 && _vectorBaseSize > 0 &&
                            (int)VectorGroupOf(featureIndex % _vectorBaseSize) == occludedGroup)
                        {
                            value = 0f;
                        }
                    }
                    else if (rayBlanked)
                    {
                        value = NeutralRayValue(featureIndex, tags);
                    }
                    tensor[0, featureIndex] = value;
                }
                _worker.SetInput(slot.InputName, tensor);
            }

            _worker.Schedule();
            var output = _worker.PeekOutput(_outputName) as Tensor<float>;
            if (output == null)
            {
                Array.Clear(actions, 0, actions.Length);
                return;
            }
            output.CompleteAllPendingOperations();
            for (int channel = 0; channel < actions.Length; channel++)
            {
                actions[channel] = output[0, channel];
            }
        }

        // -- Lifetime --------------------------------------------------------

        void Release()
        {
            Ready = false;
            for (int slotIndex = 0; slotIndex < _slots.Count; slotIndex++)
            {
                if (_slots[slotIndex].Tensor != null) _slots[slotIndex].Tensor.Dispose();
            }
            _slots.Clear();
            if (_worker != null)
            {
                _worker.Dispose();
                _worker = null;
            }
            Subject = null;
            BoundModel = null;
            Array.Clear(_shares, 0, _shares.Length);
            Peak = 0f;
        }

        public void Dispose() => Release();
    }
}
