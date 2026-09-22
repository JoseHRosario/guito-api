using GuitoApi.DataTransferObjects.Output;
using System.Text.Json;

namespace GuitoApi.Services.Account
{
    public class ListTransactionsDummyService : IListTransactionsService
    {
        public Task<TransactionList> List(DateTime? dateFrom, DateTime? dateTo)
        {
            var output = new TransactionList();
            var response = @"{
    ""transactions"": {
        ""booked"": [
            {
                ""transactionId"": ""2024041212024-04-12-04.13.11.550042"",
                ""bookingDate"": ""2024-04-12"",
                ""valueDate"": ""2024-04-12"",
                ""transactionAmount"": {
                    ""amount"": ""-77.93"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""DD PT73101243 MEO, SA         44993717555"",
                ""internalTransactionId"": ""a7e0aa715162512a4bf014c564563607""
            },
            {
                ""transactionId"": ""2024041212024-04-12-04.13.11.549970"",
                ""bookingDate"": ""2024-04-12"",
                ""valueDate"": ""2024-04-12"",
                ""transactionAmount"": {
                    ""amount"": ""-27.99"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""VIS PAGAMENTO CARTAO DE CREDITO"",
                ""internalTransactionId"": ""f6e0a98e67b82deb20fb89f3fc0a9a6c""
            },
            {
                ""transactionId"": ""2024041132024-04-11-09.30.20.272826"",
                ""bookingDate"": ""2024-04-11"",
                ""valueDate"": ""2024-04-11"",
                ""transactionAmount"": {
                    ""amount"": ""-70.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""TRF P  jose rosario"",
                ""internalTransactionId"": ""a9e7b44698ed47ccb791944c720b318e""
            },
            {
                ""transactionId"": ""2024041032024-04-10-15.23.37.829125"",
                ""bookingDate"": ""2024-04-10"",
                ""valueDate"": ""2024-04-10"",
                ""transactionAmount"": {
                    ""amount"": ""-104.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 9166 CLINICA DO PELO LT 3 LJ CONTACTLESS"",
                ""internalTransactionId"": ""42f6efe96c9a8c86614bf7c1853b907c""
            },
            {
                ""transactionId"": ""2024040932024-04-09-12.48.39.054787"",
                ""bookingDate"": ""2024-04-09"",
                ""valueDate"": ""2024-04-09"",
                ""transactionAmount"": {
                    ""amount"": ""-75.6"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 4967 IFTHENPAY SANTA MARIA DE LAMAS"",
                ""internalTransactionId"": ""cd6132ce26ef9cd5f7e08f7e881b55fe""
            },
            {
                ""transactionId"": ""2024040932024-04-09-12.39.48.528392"",
                ""bookingDate"": ""2024-04-09"",
                ""valueDate"": ""2024-04-09"",
                ""transactionAmount"": {
                    ""amount"": ""-32.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""TRF MB WAY P       9046"",
                ""internalTransactionId"": ""da384f7bbb1c4b99663dd0e134c3960b""
            },
            {
                ""transactionId"": ""2024040912024-04-09-04.15.17.821543"",
                ""bookingDate"": ""2024-04-09"",
                ""valueDate"": ""2024-04-09"",
                ""transactionAmount"": {
                    ""amount"": ""-3.9"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 9166 Gelataria Arcobianco Od CONTACTLESS"",
                ""internalTransactionId"": ""cf0db148f1a68ecaced0a30bcd18ba11""
            },
            {
                ""transactionId"": ""2024040852024-04-09-00.08.33.991347"",
                ""bookingDate"": ""2024-04-08"",
                ""valueDate"": ""2024-04-08"",
                ""transactionAmount"": {
                    ""amount"": ""-2.1"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""PAG BXVAL- 4967 VIAVERDE"",
                ""internalTransactionId"": ""70d62b5fef1ccd77931abed9ba063301""
            },
            {
                ""transactionId"": ""2024040812024-04-08-00.20.52.215430"",
                ""bookingDate"": ""2024-04-08"",
                ""valueDate"": ""2024-04-08"",
                ""transactionAmount"": {
                    ""amount"": ""-4.33"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""DDPT48100529 SCP          92102711831"",
                ""internalTransactionId"": ""7ec76ea2781aa2084948ab6a3352ca48""
            },
            {
                ""transactionId"": ""2024040802024-04-07-18.28.21.129281"",
                ""bookingDate"": ""2024-04-08"",
                ""valueDate"": ""2024-04-08"",
                ""transactionAmount"": {
                    ""amount"": ""-7.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 9166 DECATHLON 2720-031 AMAD CONTACTLESS"",
                ""internalTransactionId"": ""e8383079b8fba16411a470f12cd7f05d""
            },
            {
                ""transactionId"": ""2024040802024-04-07-16.47.49.008394"",
                ""bookingDate"": ""2024-04-08"",
                ""valueDate"": ""2024-04-08"",
                ""transactionAmount"": {
                    ""amount"": ""-28.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 9166 SCP ALVALADE            CONTACTLESS"",
                ""internalTransactionId"": ""ba8c117092a4360b83da86a9ceb50404""
            },
            {
                ""transactionId"": ""2024040802024-04-06-12.56.46.527287"",
                ""bookingDate"": ""2024-04-08"",
                ""valueDate"": ""2024-04-08"",
                ""transactionAmount"": {
                    ""amount"": ""-12.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 9166 ARRAIAL POETICO - UNAMA CONTACTLESS"",
                ""internalTransactionId"": ""3cc18730c54e88264912e401486be561""
            },
            {
                ""transactionId"": ""2024040802024-04-07-12.10.16.842237"",
                ""bookingDate"": ""2024-04-08"",
                ""valueDate"": ""2024-04-07"",
                ""transactionAmount"": {
                    ""amount"": ""-300.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""TRF P  Jose Rosario"",
                ""internalTransactionId"": ""6a8912536cd7e6aedbd8be7bd1d17b07""
            },
            {
                ""transactionId"": ""2024040532024-04-05-18.13.10.671714"",
                ""bookingDate"": ""2024-04-05"",
                ""valueDate"": ""2024-04-05"",
                ""transactionAmount"": {
                    ""amount"": ""-10.89"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 9166 A QUINTA LUGAR LDA ODIV CONTACTLESS"",
                ""internalTransactionId"": ""bc1bf7d4800eb22e7e35f83f0ec94b2a""
            },
            {
                ""transactionId"": ""2024040532024-04-05-08.04.11.033625"",
                ""bookingDate"": ""2024-04-05"",
                ""valueDate"": ""2024-04-05"",
                ""transactionAmount"": {
                    ""amount"": ""+300.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""TRF DE Jose Henrique"",
                ""internalTransactionId"": ""b29a9f9fd12eb67c9116f7781e65db5d""
            },
            {
                ""transactionId"": ""2024040412024-04-04-04.14.26.790750"",
                ""bookingDate"": ""2024-04-04"",
                ""valueDate"": ""2024-04-04"",
                ""transactionAmount"": {
                    ""amount"": ""-28.89"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""DD PT50106022 SIMAR           00185179398"",
                ""internalTransactionId"": ""0f6ccdeb3babfecc628effe54165298b""
            },
            {
                ""transactionId"": ""2024040332024-04-03-16.47.50.885128"",
                ""bookingDate"": ""2024-04-03"",
                ""valueDate"": ""2024-04-03"",
                ""transactionAmount"": {
                    ""amount"": ""-74.24"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 9166 EST SERVICO A S ALCOCHETE ALCOCHETE"",
                ""internalTransactionId"": ""d8a75e4f4e66e13bf0ae9acf8e152a73""
            },
            {
                ""transactionId"": ""2024040312024-04-03-04.05.18.111170"",
                ""bookingDate"": ""2024-04-03"",
                ""valueDate"": ""2024-04-03"",
                ""transactionAmount"": {
                    ""amount"": ""-14.8"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 9166 UCI CINEMAS WEBKIOSK LI CONTACTLESS"",
                ""internalTransactionId"": ""c7813af1d31c99156de02c7f33ef2bd9""
            },
            {
                ""transactionId"": ""2024040312024-04-03-04.05.18.109338"",
                ""bookingDate"": ""2024-04-03"",
                ""valueDate"": ""2024-04-03"",
                ""transactionAmount"": {
                    ""amount"": ""+20.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""TRF. P O  MULTICARE T13761"",
                ""internalTransactionId"": ""994dbe5ddb40bbd811a9f04294dfefa7""
            },
            {
                ""transactionId"": ""2024040252024-04-03-00.02.13.429160"",
                ""bookingDate"": ""2024-04-02"",
                ""valueDate"": ""2024-04-02"",
                ""transactionAmount"": {
                    ""amount"": ""-138.15"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""DD PT96113924 AT - AUTORIDADE AT202300000000141"",
                ""internalTransactionId"": ""a1fc470bb8204679c5372f859c1e0913""
            },
            {
                ""transactionId"": ""2024040252024-04-03-00.02.13.429071"",
                ""bookingDate"": ""2024-04-02"",
                ""valueDate"": ""2024-04-02"",
                ""transactionAmount"": {
                    ""amount"": ""-89.14"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""DD PT84105798 Petrogal        00000411167"",
                ""internalTransactionId"": ""e30630c53456ecd3ee26de1b891b9de4""
            },
            {
                ""transactionId"": ""2024040232024-04-02-10.18.46.744381"",
                ""bookingDate"": ""2024-04-02"",
                ""valueDate"": ""2024-04-02"",
                ""transactionAmount"": {
                    ""amount"": ""-32.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""TRF MB WAY P       9046"",
                ""internalTransactionId"": ""36e567b758a51b4df37be8483d302ec8""
            },
            {
                ""transactionId"": ""2024040232024-04-02-10.09.35.172181"",
                ""bookingDate"": ""2024-04-02"",
                ""valueDate"": ""2024-04-02"",
                ""transactionAmount"": {
                    ""amount"": ""+1500.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""TRF DE Jose Rosario"",
                ""internalTransactionId"": ""4ad28d5cdf83218dc7c800632b1e40ce""
            },
            {
                ""transactionId"": ""2024040212024-04-02-05.15.29.745926"",
                ""bookingDate"": ""2024-04-02"",
                ""valueDate"": ""2024-04-02"",
                ""transactionAmount"": {
                    ""amount"": ""-13.8"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 4967 UCI CINEMAS LISBOA"",
                ""internalTransactionId"": ""85ba398afd2296b4674b2aa251be4282""
            },
            {
                ""transactionId"": ""2024040212024-04-02-05.15.29.745869"",
                ""bookingDate"": ""2024-04-02"",
                ""valueDate"": ""2024-04-02"",
                ""transactionAmount"": {
                    ""amount"": ""-10.0"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""LEV ATM 9166 RED 6000 CAJAS DE AHORROS"",
                ""internalTransactionId"": ""98f67fe75a5376b59ebc8a211e3aa2c8""
            },
            {
                ""transactionId"": ""2024040212024-04-02-05.15.29.745810"",
                ""bookingDate"": ""2024-04-02"",
                ""valueDate"": ""2024-04-02"",
                ""transactionAmount"": {
                    ""amount"": ""-12.56"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""DDPT57107254 DECO PROTEST 03107647826"",
                ""internalTransactionId"": ""6856e3d4d2ef088844327a2655bd34a7""
            },
            {
                ""transactionId"": ""2024040152024-04-02-00.11.45.027388"",
                ""bookingDate"": ""2024-04-01"",
                ""valueDate"": ""2024-04-01"",
                ""transactionAmount"": {
                    ""amount"": ""-4.2"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""PAG BXVAL- 4967 VIAVERDE"",
                ""internalTransactionId"": ""53b5b1fe555e8357a054eaa72e2ae8e0""
            },
            {
                ""transactionId"": ""2024040132024-04-01-18.28.34.384999"",
                ""bookingDate"": ""2024-04-01"",
                ""valueDate"": ""2024-04-01"",
                ""transactionAmount"": {
                    ""amount"": ""-12.91"",
                    ""currency"": ""EUR""
                },
                ""remittanceInformationUnstructured"": ""COMPRA 9166 AUCHAN AMADORA AMADORA  CONTACTLESS"",
                ""internalTransactionId"": ""7a4efcf57691b2d36d3754ec2ed71cf9""
            }
		],
        ""pending"": []
    }
}";
            var document = JsonDocument.Parse(response);
            var transactions = document.RootElement
                .GetProperty("transactions")
                .GetProperty("booked");
            foreach (var transaction in transactions.EnumerateArray())
            {
                var transactionAmount = transaction
                    .GetProperty("transactionAmount")
                    .GetProperty("amount").GetString();
                if (transactionAmount == null)
                    continue;

                var amount = decimal.Parse(transactionAmount);
                // We only want debits
                if (amount > 0)
                    continue;

#pragma warning disable CS8604 // Possible null reference argument.
                var transactionDetail = new TransactionListDetail
                {
                    Amount = amount * -1,
                    Date = transaction.GetProperty("bookingDate").GetString() == null
                        ? null
                        : DateTime.Parse(transaction.GetProperty("bookingDate").GetString()),
                    Description = transaction.GetProperty("remittanceInformationUnstructured").GetString(),
                    Id = transaction.GetProperty("internalTransactionId").GetString()
                };
#pragma warning restore CS8604 // Possible null reference argument.
                output.Transactions.Add(transactionDetail);
            }
            return Task.FromResult(output);
        }

    }
}
