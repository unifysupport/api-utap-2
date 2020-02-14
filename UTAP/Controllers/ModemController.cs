using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;

namespace UTAP.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ModemController : ControllerBase
    {
        [HttpPost("send")]
        public IActionResult SendMessage([FromBody] TextMessage message, [FromServices] ATSquirter atSquirter)
        {
            try
            {
                atSquirter.SendSMS(message);
                return Ok();
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        [HttpGet("devices")]
        public IActionResult GetSIMs([FromServices] ATSquirter atSquirter)
        {
            try
            {
                return Ok(atSquirter.SimCards.Select(s => s.PhoneNumber));
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        [HttpPost("devices/{SimNumber}")]
        public IActionResult NewSim(string SimNumber, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                arbiter.StartNewSimQueue(SimNumber);
                return Ok();
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        [HttpDelete("devices/{SimNumber}")]
        public IActionResult DisconnectSim(string SimNumber, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                arbiter.DisconnectedSIMs.Add(SimNumber);
                return Ok();
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        [HttpPut("devices/{SimNumber}")]
        public IActionResult ConnectSim(string SimNumber, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                if (arbiter.DisconnectedSIMs.Contains(SimNumber)) arbiter.DisconnectedSIMs.Remove(SimNumber);
                return Ok();
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        [HttpPost("receive")]
        public IActionResult ReceiveMessage([FromBody] SMS Message, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                arbiter.StoreInboundMessage(Message);
                return Ok();
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }
    }
}
