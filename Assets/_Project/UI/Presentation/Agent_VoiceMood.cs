namespace PoSoccer
{
    /// <summary>
    /// The five emotions a character can voice (tournament standard voice matrix,
    /// 2026-11-22: angry, happy, hurt, insult, celebrate x 5 levels). Values are part
    /// of the WAV naming contract and the synth seed: append, never renumber.
    /// </summary>
    public enum Agent_VoiceMood
    {
        Happy = 0,
        Angry = 1,
        Hurt = 2,
        Insult = 3,
        Celebrate = 4,
    }
}
