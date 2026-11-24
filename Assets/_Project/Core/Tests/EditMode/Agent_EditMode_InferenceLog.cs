using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>Each brain prints its "&lt;brain&gt;.onnx inference running" line once per session.</summary>
    public sealed class Agent_EditMode_InferenceLog
    {
        [SetUp]
        public void SetUp() => Agent_InferenceLog.Clear();

        [TearDown]
        public void TearDown() => Agent_InferenceLog.Clear();

        [Test]
        public void EachBrainLogsOnce()
        {
            LogAssert.Expect(LogType.Log, Agent_InferenceLog.Line("KIM", "Blue"));
            Assert.IsTrue(Agent_InferenceLog.Note("KIM", "Blue"));
            Assert.IsFalse(Agent_InferenceLog.Note("KIM", "Red"), "a second KIM decision logs nothing");
            LogAssert.Expect(LogType.Log, Agent_InferenceLog.Line("NICK", "Red"));
            Assert.IsTrue(Agent_InferenceLog.Note("NICK", "Red"));
            Assert.IsFalse(Agent_InferenceLog.Note("", "Blue"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void LineCarriesTheBrainFileAndMarker()
        {
            StringAssert.Contains("KIM.onnx inference running", Agent_InferenceLog.Line("KIM", "x"));
        }
    }
}
