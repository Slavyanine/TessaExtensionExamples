using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Chronos.Contracts;
using NLog;
using Tessa.Notices;
using Tessa.Platform;
using Tessa.Platform.Data;
using Tessa.Platform.Validation;
using Unity;

namespace Tessa.Extensions.Chronos.Plugins
{
    /// <summary>
    /// Пример плагина, который может работать через серверное API.
    /// </summary>
    [Plugin(
        Name = "PnrPartnerNoticeCronPlugin",
        Description = "Плагин отправляет уведомление ответственному сотуднику об окончании срока согласования КА",
        Version = 1,
        ConfigFile = ConfigFilePath)]
    public sealed class PnrPartnerNoticeCronPlugin :
        Plugin
    {
        #region Constants

        /// <summary>
        /// Относительный путь к конфигурационному файлу плагина.
        /// </summary>
        private const string ConfigFilePath = "configuration/PnrPartnerNoticeCronPlugin.xml";

        //введено
        private const string MessageFormatOut = @" 
        <html>
        <body>
        <tr>
                <td> <span><br/>Уведомляем вас, что срок согласования контрагента подходит к концу:</span> </td>
            </tr>
         <tr>
                <td> <span><br/>Наименование контрагента: {0}</span> </td>
            </tr>
        <tr>
                <td> <span><br/>ИНН: {1}</span> </td>
            </tr>
        <tr>
                <td> <span><br/>КПП: {2}</span> </td>
            </tr>
        <tr>
                <td> <span><br/>Срок согласования: {3}</span> </td>
            </tr>
        </body>
        </html>
        ";


        #endregion

        #region Fields

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private IMailService mailService;

        public class PartnerNotice
        {
            public string To { get; set; }
            public string Subject { get; set; }
            public string Partner { get; set; }
            public string INN { get; set; }
            public string KPP { get; set; }
            public string Validity { get; set; }
        }

        #endregion

        #region Base Overrides

        public override async Task EntryPointAsync(CancellationToken cancellationToken = default)
        {
            logger.Trace("Starting plugin PnrPartnerNoticeCronPlugin");

            // конфигурируем контейнер Unity для использования стандартных серверных API (в т.ч. API карточек)
            // а также для получения прямого доступа к базе данных через IDbScope по строке подключения из app.config;
            // предполагаем, что все действия, совершаемые плагином, будут выполняться от имени пользователя System
            logger.Trace("Configuring container");

            TessaPlatform.InitializeFromConfiguration();

            IUnityContainer container = await new UnityContainer()


                .RegisterServerForPluginAsync()
                ;



            IDbScope dbScope = container.Resolve<IDbScope>();
            await using (dbScope.Create())
            {
                // работа в пределах одного SQL-соединения, транзакция при этом явно не создаётся

                if (this.StopRequested)
                {
                    // была запрошена асинхронная остановка, можно периодически проверять значение этого свойства,
                    // и консистентно завершать выполнение (закрыть транзакцию, если была открыта, и др.)
                    return;
                }

                var db = dbScope.Db;

                // вычисляем контрагентов 
                var ParthersIDs = await db
                    .SetCommand(@"
                        SELECT [p].[ID]
                        FROM [Partners] [p] with(nolock)
                        WHERE 1 = 1
                        AND [p].[Validity] IS NOT NULL
                        AND (DATEDIFF(day, GETDATE(), [p].[Validity])=60 OR DATEDIFF(day, GETDATE(), [p].[Validity])=30)
                        AND [p].[StatusID]=0                
                    ")
                    .LogCommand()
                    .ExecuteListAsync<Guid>(cancellationToken);

                var letters = new List<PartnerNotice>();

                foreach (var partherID in ParthersIDs)
                {
                    var letter = await db
                    .SetCommand(@"SELECT top(1) [pr].[Email]                          AS 'To',
                                                   'Окончание срока согласования контрагента ' + [ppr].[PartnerName] AS Subject,
                                                   [ppr].[PartnerName]  AS Partner,
                                                   COALESCE([p].[INN], 'не указан') AS INN,
                                                   COALESCE([p].[KPP], 'не указан') AS KPP,
                                                   CONVERT(nvarchar, FORMAT([p].[Validity], 'dd.MM.yyyy', 'ru-RU' ))   AS Validity
                                                   FROM [PnrPartnerRequests] [ppr] WITH (NOLOCK)
                                                   INNER JOIN [DocumentCommonInfo] [dci] WITH (NOLOCK)
                                                               ON [dci].[ID] = [ppr].[ID]
                                                   INNER JOIN [PersonalRoles] [pr] WITH (NOLOCK)
                                                               ON [pr].[ID] = [dci].[AuthorID]
                                                   INNER JOIN [Partners] [p] WITH (NOLOCK)
                                                               ON [p].[ID] = [ppr].[PartnerID]
                                                   INNER JOIN [FdSatelliteCommonInfo] as [fsci] WITH (NOLOCK)
                                                                ON [fsci].[MainCardId] = [ppr].[ID]
                                            WHERE 1 = 1
                                              AND [ppr].[PartnerID] = @partherID
                                              AND [fsci].[StateName] != 'Проект'
                                              AND [pr].[Email] LIKE '%@%.%'
                                            ORDER BY [dci].[CreationDate] DESC
                                            ",
                        db.Parameter("@partherID", partherID))
                    .LogCommand()
                    .ExecuteAsync<PartnerNotice>(cancellationToken);

                    if (letter != null)
                    {
                        letters.Add(letter);
                    }

                }


                this.mailService = container.Resolve<IMailService>();



                foreach (var letter in letters)
                {
                    await mailService.PostMessageAsync(letter.To, letter.Subject, string.Format(MessageFormatOut, letter.Partner, letter.INN, letter.KPP, letter.Validity), new ValidationResultBuilder());
                }


                letters.Clear();
                ParthersIDs.Clear();
            }
                
            logger.Trace("Shutting down PnrPartnerNoticeCronPlugin");
        }
        #endregion
    }
}
