using System;

namespace Home.Data.Models.Vk
{
    public class SimpleUserActivity
    {
        public int UserId { get; set; }
        public string UserName { get; set; }
        public string CurrentStatus { get; set; }
        public int VisitsCount { get; set; }
        public TimeSpan TimeInSite { get; set; }
        public TimeSpan TimeInApp { get; set; }
    }
}
