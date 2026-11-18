using System;

namespace ShouTech.Domain
{
    /// <summary>
    /// </summary>
    public class Pref
    {
        public string Lang { get; set; } = "en";

        public bool IsFirst { get; set; } = true;

        public string LastCo { get; set; } = "";

        public DateTime Updated { get; set; } = DateTime.Now;
    }
}