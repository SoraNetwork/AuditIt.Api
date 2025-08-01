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
    public class ItemDefinitionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public ItemDefinitionsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: api/ItemDefinitions
        [HttpGet]
        public async Task<ActionResult<IEnumerable<ItemDefinition>>> GetItemDefinitions()
        {
            return await _context.ItemDefinitions.Include(i => i.Category).ToListAsync();
        }

        // GET: api/ItemDefinitions/5
        [HttpGet("{id}")]
        public async Task<ActionResult<ItemDefinition>> GetItemDefinition(int id)
        {
            var itemDefinition = await _context.ItemDefinitions.Include(i => i.Category).FirstOrDefaultAsync(i => i.Id == id);

            if (itemDefinition == null)
            {
                return NotFound();
            }

            return itemDefinition;
        }

        // PUT: api/ItemDefinitions/5
        [HttpPut("{id}")]
        public async Task<IActionResult> PutItemDefinition(int id, ItemDefinition itemDefinition)
        {
            if (id != itemDefinition.Id)
            {
                return BadRequest();
            }

            _context.Entry(itemDefinition).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!ItemDefinitionExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return NoContent();
        }

        // POST: api/ItemDefinitions
        [HttpPost]
        public async Task<ActionResult<ItemDefinition>> PostItemDefinition(CreateItemDefinitionDto itemDefinitionDto)
        {
            var category = await _context.Categories.FindAsync(itemDefinitionDto.CategoryId);
            if (category == null)
            {
                return BadRequest("Invalid CategoryId");
            }

            var itemDefinition = new ItemDefinition
            {
                Name = itemDefinitionDto.Name,
                CategoryId = itemDefinitionDto.CategoryId,
                Unit = itemDefinitionDto.Unit,
                Description = itemDefinitionDto.Description,
                CreatedAt = DateTime.UtcNow
            };

            _context.ItemDefinitions.Add(itemDefinition);
            await _context.SaveChangesAsync();

            // Load the category to include it in the response
            await _context.Entry(itemDefinition).Reference(i => i.Category).LoadAsync();

            return CreatedAtAction("GetItemDefinition", new { id = itemDefinition.Id }, itemDefinition);
        }

        // DELETE: api/ItemDefinitions/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteItemDefinition(int id)
        {
            var itemDefinition = await _context.ItemDefinitions.FindAsync(id);
            if (itemDefinition == null)
            {
                return NotFound();
            }

            _context.ItemDefinitions.Remove(itemDefinition);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        private bool ItemDefinitionExists(int id)
        {
            return _context.ItemDefinitions.Any(e => e.Id == id);
        }
    }
}
