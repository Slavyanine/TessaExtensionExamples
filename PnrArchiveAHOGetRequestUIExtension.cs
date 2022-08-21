using System;
using System.Threading.Tasks;
using Tessa.Cards;
using Tessa.Extensions.Client.Helpers;
using Tessa.Extensions.Shared.Info;
using Tessa.Extensions.Shared.PnrCards;
using Tessa.Platform.Runtime;
using Tessa.Platform.Storage;
using Tessa.UI;
using Tessa.UI.Cards;

namespace Tessa.Extensions.Client.UI
{
	/// <summary>
	/// Заявка на получение документов в архив
	/// </summary>
	public sealed class PnrArchiveAHOGetRequestUIExtension : CardUIExtension
	{
		private readonly ISession session;
		private readonly ICardRepository cardRepository;


		public PnrArchiveAHOGetRequestUIExtension(ISession session, ICardRepository cardRepository)
		{
			this.session = session;
			this.cardRepository = cardRepository;

		}

		private async void SetUserDepartmentInfo(Guid? authorID, ICardModel cardModel)
		{
			CardRequest request = new CardRequest
			{
				RequestType = Shared.PnrRequestTypes.GetUserDepartmentInfoRequestTypeID,
				Info =
				{
					{ "authorID", authorID }
				}
			};

			CardResponse response = await cardRepository.RequestAsync(request);

			Tessa.Platform.Validation.ValidationResult result = response.ValidationResult.Build();
			TessaDialog.ShowNotEmpty(result);
			if (result.IsSuccessful)
			{
				
					cardModel.Card.Sections["PnrArchiveAHOGetRequest"].Fields["DepartmentID"] = response.Info.Get<Guid?>("DepartmentID");
					cardModel.Card.Sections["PnrArchiveAHOGetRequest"].Fields["DepartmentName"] = response.Info.Get<string>("Name");
					cardModel.Card.Sections["PnrArchiveAHOGetRequest"].Fields["DepartmentIdx"] = response.Info.Get<string>("Index");

			}
		}


		// Установка видимости контролов, зависимых от Вида места хранения
		private void SetStorageDependenceVisibility(ICardModel cardModel, Guid? StorageType)
		{
			// Корреспонденты
			ClientHelper.ControlVisibleOrHide(cardModel, "Box", StorageType == PnrStorageTypes.PnrStorageTypeCOID);

			// ФИО Корреспондента
			ClientHelper.ControlVisibleOrHide(cardModel, "Barcode", StorageType == PnrStorageTypes.PnrStorageTypeDelisID);
		}


		public override async Task Initialized(ICardUIExtensionContext context)
		{
			Card card = context.Card;
			ICardModel cardModel = context.Model;
			StringDictionaryStorage<CardSection> sections;

			if ((cardModel = context.Model) == null
				|| cardModel.InSpecialMode()
				|| (card = cardModel.Card) == null
				|| (sections = card.TryGetSections()) == null
				|| !sections.TryGetValue(SchemeInfo.PnrArchiveAHOGetRequest, out CardSection mainSection))
			{
				return;
			}

			SetStorageDependenceVisibility(cardModel, mainSection.Fields.TryGet<Guid?>(SchemeInfo.PnrArchiveAHOGetRequest.StorageID));

			mainSection.FieldChanged += async (s, e) =>
			{
				// Вид входящего документа: Visibility контролов
				if (e.FieldName == SchemeInfo.PnrArchiveAHOGetRequest.StorageID && e.FieldValue != null)
				{
					SetStorageDependenceVisibility(cardModel, (Guid)e.FieldValue);
				}
			};


			if (card.StoreMode == CardStoreMode.Insert)
			{
				
				// начальная инициализация поля Подразделение по автору
				SetUserDepartmentInfo((Guid?)card.Sections["DocumentCommonInfo"].Fields["AuthorID"], cardModel);
			}

			

			return;
		}
	}
}
