#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace WarehouseManager.Inventory
{
    internal static class PackagingInfo
    {
        internal struct Info
        {
            public bool HasBig;
            public bool HasSmall;
            public int UpbBig;
            public int UpbSmall;

            public string BoxTypeText
            {
                get
                {
                    if (HasBig && HasSmall) return "Big+Small";
                    if (HasBig) return "Big";
                    if (HasSmall) return "Small";
                    return "Unknown";
                }
            }
        }

        internal static Info Get(EItemType itemType)
        {
            var info = new Info();

            try
            {
                // Try both (even if you THINK everything is big)
                int upbBig = RestockManager.GetMaxItemCountInBox(itemType, true);
                int upbSmall = RestockManager.GetMaxItemCountInBox(itemType, false);

                info.UpbBig = Mathf.Max(0, upbBig);
                info.UpbSmall = Mathf.Max(0, upbSmall);

                info.HasBig = info.UpbBig > 0;
                info.HasSmall = info.UpbSmall > 0;
            }
            catch
            {
                // If RestockManager isn't available in some state, leave Unknown.
            }

            return info;
        }
    }
}
