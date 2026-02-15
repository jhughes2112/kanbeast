using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using KanBeast.Server.Hubs;
using KanBeast.Server.Models;
using KanBeast.Server.Services;
using KanBeast.Shared;

namespace KanBeast.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;
    private readonly IHubContext<KanbanHub, IKanbanHubClient> _hubContext;

    public SettingsController(ISettingsService settingsService, IHubContext<KanbanHub, IKanbanHubClient> hubContext)
    {
        _settingsService = settingsService;
        _hubContext = hubContext;
    }

    [HttpGet]
    public async Task<ActionResult<Settings>> GetSettings()
    {
        var settings = await _settingsService.GetSettingsAsync();
        return Ok(settings);
    }

    [HttpPut]
    public async Task<ActionResult<Settings>> UpdateSettings([FromBody] Settings settings)
    {
        Settings updatedSettings = await _settingsService.UpdateSettingsAsync(settings);
        await _hubContext.Clients.All.SettingsUpdated(updatedSettings.LLMConfigs);
        return Ok(updatedSettings);
    }

    [HttpPost("llm")]
    public async Task<ActionResult<LLMConfig>> AddLLMConfig([FromBody] LLMConfig config)
    {
        LLMConfig? addedConfig = await _settingsService.AddLLMConfigAsync(config);
        Settings current = await _settingsService.GetSettingsAsync();
        await _hubContext.Clients.All.SettingsUpdated(current.LLMConfigs);
        return Ok(addedConfig);
    }

    [HttpDelete("llm/{id}")]
    public async Task<ActionResult> RemoveLLMConfig(string id)
    {
        bool result = await _settingsService.RemoveLLMConfigAsync(id);
        if (!result)
        {
            return NotFound();
        }

        Settings current = await _settingsService.GetSettingsAsync();
        await _hubContext.Clients.All.SettingsUpdated(current.LLMConfigs);
        return NoContent();
    }
}
