// cod/cor/invsvc.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShouTech.Core.Inv
{
    public interface IInventoryService
    {
        Task<bool> ProcessPOSSaleAsync(POSSaleRequest req);
    }
}