using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.AspNetCore.Authorization;

namespace AuditIt.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class AuditLogsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public AuditLogsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: api/AuditLogs
        [HttpGet]
        public async Task<ActionResult<IEnumerable<AuditLog>>> GetAuditLogs([FromQuery] Guid? itemId)
        {
            var query = _context.AuditLogs.AsQueryable();

            if (itemId.HasValue)
            {
                query = query.Where(log => log.ItemId == itemId.Value);
            }

            return await query.OrderByDescending(log => log.Timestamp).ToListAsync();
        }
    }
}
