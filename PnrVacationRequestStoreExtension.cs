using System;
using System.Threading.Tasks;
using Tessa.Cards;
using Tessa.Cards.Extensions;
using Tessa.Extensions.Server.DataHelpers;
using Tessa.Extensions.Server.Helpers;
using Tessa.Extensions.Shared.Helpers;
using Tessa.Extensions.Shared.Info;
using Tessa.Extensions.Shared.PnrCards;
using Tessa.Platform.Data;
using Tessa.Platform.Runtime;
using Tessa.Platform.Storage;
using Tessa.Platform.Validation;

namespace Tessa.Extensions.Server.PnrCards
{
	public sealed class PnrVacationRequestStoreExtension : CardStoreExtension
	{
		#region Private Methods

		/// <summary>
		/// Проверка заполненности полей Заявки на отпуск
		/// </summary>
		/// <param name="validationResult"></param>
		/// <param name="dbScope"></param>
		/// <param name="session"></param>
		/// <param name="card"></param>
		/// <returns></returns>
		private async Task ValidateVacationRequestAsync(IValidationResultBuilder validationResult, IDbScope dbScope, ISession session, Card card)
		{
			var isCurrentUserAdmin = session.User.IsAdministrator();
			var isCurrentUserHR = await PnrIsUserInRole.GetIsUserInRole(dbScope, session.User.ID, PnrRoles.HrID);

			var values = await ServerHelper
								.GetRealFieldValues(
									dbScope,
									card,
									SchemeInfo.PnrRequests,
									SchemeInfo.PnrRequests.VacationCategoryID,
									SchemeInfo.PnrRequests.FirstDate,
									SchemeInfo.PnrRequests.ConnectRoaming,
									SchemeInfo.PnrRequests.Country,
									SchemeInfo.PnrRequests.Comment
								);

			var categoryID = values.TryGet<Guid?>(SchemeInfo.PnrRequests.VacationCategoryID);
			var firstDate = values.TryGet<DateTime?>(SchemeInfo.PnrRequests.FirstDate);
			var roaming = values.TryGet<bool>(SchemeInfo.PnrRequests.ConnectRoaming);
			var country = values.TryGet<string>(SchemeInfo.PnrRequests.Country);
			var comment = values.TryGet<string>(SchemeInfo.PnrRequests.Comment);

			// Проверка заполнения страны в зависимости от подключения роуминга
			if (roaming)
			{
				PnrCardFieldValidationHelper.Validate(validationResult, country, "Страна");
			}

			// Если это Очередной отпуск, то проверяем дату создания заявки
			if (firstDate.HasValue && categoryID.Equals(PnrVacationRequestTypes.PnrRegularTypeID))
			{
				DateTime? creationDate = await PnrDataHelper.GetActualFieldValueAsync<DateTime?>(dbScope, card, SchemeInfo.DocumentCommonInfo, SchemeInfo.DocumentCommonInfo.CreationDate);

				if (creationDate.HasValue)
				{
					int dayOfWeekMonday = ((int)DayOfWeek.Monday + 6) % 7; // поправка на начало рабочей недели с понедельника
					int dayOfWeekFirstDate = ((int)((DateTime)firstDate).DayOfWeek + 6) % 7;
					int delta = dayOfWeekMonday - dayOfWeekFirstDate;
					DateTime monday = ((DateTime)firstDate).AddDays(delta);
					TimeSpan? daysCount = monday - creationDate;

					if (daysCount.Value.TotalDays <= 6 && !isCurrentUserHR)
					{
						validationResult.AddError("Заявка должна быть подана не позднее понедельника недели, предшествующей отпуску.");
					}
				}
			}

			// Если это учебный отпуск или другое, то проверяем наличие файлов
			if (categoryID.Equals(PnrVacationRequestTypes.PnrStudyTypeID) ||
				categoryID.Equals(PnrVacationRequestTypes.PnrOtherTypeID))
			{
				ListStorage<CardFile> files = card.TryGetFiles();

				if (files != null && files.Count == 0 && (await ServerHelper.GetNumberOfFiles(dbScope, card.ID)) == 0 && !isCurrentUserHR)
				{
					validationResult.AddError(categoryID.Equals(PnrVacationRequestTypes.PnrStudyTypeID) ? "К документу должна быть приложена копия подтверждающего документа." : "К документу должна быть приложена копия документа - основание для отпуска.");
				}

				// Для другого вида отпуска требуем заполнения ещё и поля Комментарий
				if (categoryID.Equals(PnrVacationRequestTypes.PnrOtherTypeID) &&
					string.IsNullOrWhiteSpace(comment))
				{
					validationResult.AddError("В поле «Комментарий» укажите причину оформления отпуска.");
				}
			}
		}

		#endregion

		#region Base Overrides

		public override async Task BeforeRequest(ICardStoreExtensionContext context)
		{
			if (!context.ValidationResult.IsSuccessful())
			{
				return;
			}

			context.Request.ForceTransaction = true;
		}

		public override async Task AfterBeginTransaction(ICardStoreExtensionContext context)
		{
			Card card;
			if (!context.ValidationResult.IsSuccessful()
				|| (card = context.Request.TryGetCard()) == null)
			{
				return;
			}

			await ValidateVacationRequestAsync(context.ValidationResult, context.DbScope, context.Session, card);
		}

		#endregion
	}
}
