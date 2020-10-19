using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BexCsharpSDK;
using BexCsharpSDK.Merchant.Request;
using BexCsharpSDK.Merchant.Response;
using BexCsharpSDK.Merchant.Security;
using BexCsharpSDK.Merchant.Token;
using BexCsharpSDK.Utils;
using BKMExpressIntegration.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace BKMExpressIntegration.Controllers
{
    public class BKMController : Controller
    {
        #region Fields

        public string MerchantId { get; set; }
        public string PrivateKey { get; set; }
        public string InstallmentUrl { get; set; }
        public string NonceUrl { get; set; }
        public BexCsharpSDK.Configuration BexConfig { get; set; }
        public BexPayment BexPayment { get; set; }

        private readonly IConfiguration _configuration;

        #endregion

        public BKMController(IConfiguration configuration)
        {
            _configuration = configuration;
            var bexEnviroment = BexCsharpSDK.Environment.Preprod;
            var appEnviroment = _configuration.GetValue<string>("Environment");

            if (appEnviroment.Equals(Environments.Pilot) || appEnviroment.Equals(Environments.Live))
            {
                InstallmentUrl = "https://myproject/bkm/installments";
                NonceUrl = "https://myproject/bkm/nonce";
                bexEnviroment = BexCsharpSDK.Environment.Production;
            }
            else
            {
                //InstallmentUrl = "https://preprod-api.bkmexpress.com/bkm/installments";
                InstallmentUrl = "http://537abd5c25bf.ngrok.io/bkm/installments";
                NonceUrl = "http://537abd5c25bf.ngrok.io/bkm/nonce";
            }

            MerchantId = _configuration.GetValue<string>("MerchantId");
            PrivateKey = _configuration.GetValue<string>("PrivateKey");
            BexConfig = new BexCsharpSDK.Configuration(bexEnviroment, MerchantId, PrivateKey);
            BexPayment = new BexPayment(BexConfig);
        }

        public IActionResult Index()
        {
            ViewBag.BexUrl = BexPayment.BaseJsUrl();
            return View();
        }

        [HttpGet]
        public string InitTicket()
        {
            Token connection = GetConnectionToken();

            TicketRequest ticketRequest = new TicketRequest.Builder()
                        .newPayment()
                        .orderId("123456")
                        .amount("5000,13")
                        .campaignCode("my campaign")
                        .installmentUrl(InstallmentUrl)
                        .nonceUrl(NonceUrl)
                        .build();

            Token ticket = BexPayment.GetMerchantService().OneTimeTicket(connection, ticketRequest);

            //Token ticket = BexPayment.GetMerchantService().OneTimeTicket(connection, "5000,13", InstallmentUrl, NonceUrl);

            return ticket.Json();
        }

        [HttpPost]
        public JsonResult Nonce([FromBody] NonceRequest request)
        {
            var ticketId = request.ticketId;
            var path = request.path;
            var nonce = request.token;
            var orderId = request.orderId;
            var signature = request.signature;
            var response = new MerchantNonceResponse();

            Token connectionToken = GetConnectionToken();

            if (EncryptionUtil.VerifyBexSignature(ticketId, signature))
            {
                response.nonce = nonce;
                response.result = true;
                response.id = path;
                NonceResultResponse nonceResult = BexPayment.GetMerchantService().SendNonceResponse(connectionToken, response);
                if (nonceResult.result.Equals("ok"))
                {
                    //db order update
                }
            }
            else
            {
                response.nonce = nonce;
                response.result = false;
                response.id = path;
                response.message = "Signature verification failed";

                NonceResultResponse nonceResult = BexPayment.GetMerchantService().SendNonceResponse(connectionToken, response);
            }

            return Json(new NonceReceivedResponse());
        }

        private Token GetConnectionToken()
        {
            return BexPayment.GetMerchantService().Login();
        }

        [HttpPost]
        public JsonResult Installments([FromBody] InstallmentRequest installmentRequest)
        {
            InstallmentsResponse installmentsResponse = new InstallmentsResponse();
            if (installmentRequest?.bin == null || installmentRequest.ticketId == null || installmentRequest.totalAmount == null || !EncryptionUtil.VerifyBexSignature(installmentRequest.ticketId, installmentRequest.signature))
            {
                installmentsResponse.error = "RequestBody fields cannot be null or signature verification failed";
                return Json(installmentsResponse);
            }
            return Json(GetInstallmentResponse(installmentRequest, installmentsResponse));
        }

        private InstallmentsResponse GetInstallmentResponse(InstallmentRequest installmentRequest, InstallmentsResponse installmentsResponse)
        {
            Dictionary<string, List<Installment>> installments = GetInstallments(installmentRequest);
            installmentsResponse.installments = installments;
            installmentsResponse.status = "ok";
            installmentsResponse.error = "";
            return installmentsResponse;
        }

        private Dictionary<string, List<Installment>> GetInstallments(InstallmentRequest installmentRequest)
        {
            Dictionary<string, List<Installment>> all = new Dictionary<string, List<Installment>>();
            double totalAmount = MoneyUtils.ToDouble(installmentRequest.totalAmount);
            foreach (BinAndBank binAndBank in installmentRequest.BinAndBanks())
            {
                string bankCode = binAndBank.bankCode;
                int count = Convert.ToInt32(bankCode.Substring(3)) + 3;//+3 extension time :D
                List<Installment> installments = new List<Installment>();
                for (int i = 1; i <= count; i++)
                {
                    Installment installment = new Installment
                    {
                        numberOfInstallment = Convert.ToString(i),
                        installmentAmount = MoneyUtils.FormatTurkishLira(totalAmount / (ulong)i),
                        totalAmount = MoneyUtils.FormatTurkishLira(totalAmount),
                        vposConfig = PrepareVposConfig(bankCode)
                    };
                    installments.Add(installment);
                }
                all.Add(binAndBank.bin, installments);
            }
            return all;
        }

        private string PrepareVposConfig(string bankCode)
        {
            var bankList = new Banks();
            VposConfig vposConfig = new VposConfig { bankIndicator = bankCode };
            if (bankList[bankCode].Equals("AKBANK"))
            {
                vposConfig.vposUserId = "akapi";
                vposConfig.vposPassword = "TEST1234";
                vposConfig.AddExtra("ClientId", "100111222");
                vposConfig.AddExtra("storekey", "TEST1234");
                // vposConfig.AddExtra("overrideOrderId", "AkbankMyCustomOrderID123");
                vposConfig.serviceUrl = "http://srvirt01:7200/akbank";
            }
            else if (bankList[bankCode].Equals("TEBBANK"))
            {
                vposConfig.vposUserId = "bkmapi";
                vposConfig.vposPassword = "KUTU8520";
                vposConfig.AddExtra("ClientId", "401562930");
                vposConfig.AddExtra("storekey", "KUTU8520");
                // vposConfig.AddExtra("overrideOrderId", "TEBMyCustomOrderID123");
                vposConfig.serviceUrl = "http://srvirt01:7200/teb";
            }
            else
            {
                // Default POS
                vposConfig.vposUserId = "bkmapi";
                vposConfig.vposPassword = "KUTU8900";
                vposConfig.AddExtra("ClientId", "700655047520");
                vposConfig.AddExtra("storekey", "TEST123456");
                // vposConfig.AddExtra("overrideOrderId", "TEBMyCustomOrderID123");
                vposConfig.serviceUrl = "http://srvirt01:7200/teb";
            }
            return EncryptionUtil.EncryptWithBex(vposConfig);
        }
    }
}
