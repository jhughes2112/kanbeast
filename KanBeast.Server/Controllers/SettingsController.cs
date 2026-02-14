using Microsoft.AspNetCore.Mvc;
using KanBeast.Server.Models;
using KanBeast.Server.Services;
using KanBeast.Shared;

namespace KanBeast.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;

    public SettingsController(ISettingsService settingsService)
    {
        _settingsService = settingsService;
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
        var updatedSettings = await _settingsService.UpdateSettingsAsync(settings);
        return Ok(updatedSettings);
    }

    [HttpPost("llm")]
    public async Task<ActionResult<LLMConfig>> AddLLMConfig([FromBody] LLMConfig config)
    {
        var addedConfig = await _settingsService.AddLLMConfigAsync(config);
        return Ok(addedConfig);
    }

    [HttpDelete("llm/{id}")]
    public async Task<ActionResult> RemoveLLMConfig(string id)
    {
        var result = await _settingsService.RemoveLLMConfigAsync(id);
        if (!result)
            return NotFound();

        return NoContent();
    }
}
