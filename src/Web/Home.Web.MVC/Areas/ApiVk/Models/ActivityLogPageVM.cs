using System.Collections.Generic;

namespace Home.Web.Areas.ApiVk.Models
{
    /// <summary> <inheritdoc/> </summary>
    public partial class ActivityLogPageVM
    {
        public ushort Page { get; set; }
        public List<ActivityLogItemVM> Items { get; set; }
    }
}
