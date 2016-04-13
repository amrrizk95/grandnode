﻿using Nop.Core.Data;
using Nop.Core.Domain.Customers;
using Nop.Services.Events;
using System;
using System.Linq;
using System.Collections.Generic;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nop.Services.Messages;
using Nop.Core.Domain.Messages;
using Nop.Core;
using Nop.Services.Stores;
using Nop.Services.Catalog;
using Nop.Services.Logging;
using Nop.Services.Localization;

namespace Nop.Services.Customers
{
    public partial class CustomerReminderService : ICustomerReminderService
    {
        #region Fields

        private readonly IRepository<CustomerReminder> _customerReminderRepository;
        private readonly IRepository<CustomerReminderHistory> _customerReminderHistoryRepository;
        private readonly IRepository<Customer> _customerRepository;
        private readonly CustomerSettings _customerSettings;
        private readonly IEventPublisher _eventPublisher;
        private readonly ITokenizer _tokenizer;
        private readonly IEmailAccountService _emailAccountService;
        private readonly IQueuedEmailService _queuedEmailService;
        private readonly IMessageTokenProvider _messageTokenProvider;
        private readonly IStoreService _storeService;
        private readonly ICustomerAttributeParser _customerAttributeParser;
        private readonly IProductService _productService;
        private readonly ICustomerActivityService _customerActivityService;
        private readonly ILocalizationService _localizationService;

        #endregion

        #region Ctor

        public CustomerReminderService(
            IRepository<CustomerReminder> customerReminderRepository,
            IRepository<CustomerReminderHistory> customerReminderHistoryRepository,
            IRepository<Customer> customerRepository,
            CustomerSettings customerSettings,
            IEventPublisher eventPublisher,
            ITokenizer tokenizer,
            IEmailAccountService emailAccountService,
            IQueuedEmailService queuedEmailService,
            IMessageTokenProvider messageTokenProvider,
            IStoreService storeService,
            IProductService productService,
            ICustomerAttributeParser customerAttributeParser,
            ICustomerActivityService customerActivityService,
            ILocalizationService localizationService)
        {
            this._customerReminderRepository = customerReminderRepository;
            this._customerReminderHistoryRepository = customerReminderHistoryRepository;
            this._customerRepository = customerRepository;
            this._customerSettings = customerSettings;
            this._eventPublisher = eventPublisher;
            this._tokenizer = tokenizer;
            this._emailAccountService = emailAccountService;
            this._messageTokenProvider = messageTokenProvider;
            this._queuedEmailService = queuedEmailService;
            this._storeService = storeService;
            this._customerAttributeParser = customerAttributeParser;
            this._productService = productService;
            this._customerActivityService = customerActivityService;
            this._localizationService = localizationService;
        }

        #endregion

        #region Utilities

        protected bool SendEmail_AbandonedCart(Customer customer, CustomerReminder customerReminder, string reminderlevelId)
        {

            var reminderLevel = customerReminder.Levels.FirstOrDefault(x => x.Id == reminderlevelId);
            var emailAccount = _emailAccountService.GetEmailAccountById(reminderLevel.EmailAccountId);
            var store = _storeService.GetStoreById(customer.ShoppingCartItems.FirstOrDefault().StoreId);

            //retrieve message template data
            var bcc = reminderLevel.BccEmailAddresses;
            var subject = reminderLevel.Subject;
            var body = reminderLevel.Body;

            var rtokens = AllowedTokens(CustomerReminderRuleEnum.AbandonedCart);
            var tokens = new List<Token>();

            _messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
            _messageTokenProvider.AddCustomerTokens(tokens, customer);
            _messageTokenProvider.AddShoppingCartTokens(tokens, customer);

            //Replace subject and body tokens 
            var subjectReplaced = _tokenizer.Replace(subject, tokens, false);
            var bodyReplaced = _tokenizer.Replace(body, tokens, true);
            //limit name length
            var toName = CommonHelper.EnsureMaximumLength(customer.GetFullName(), 300);
            var email = new QueuedEmail
            {
                Priority = QueuedEmailPriority.High,
                From = emailAccount.Email,
                FromName = emailAccount.DisplayName,
                To = customer.Email,
                ToName = toName,
                ReplyTo = string.Empty,
                ReplyToName = string.Empty,
                CC = string.Empty,
                Bcc = bcc,
                Subject = subjectReplaced,
                Body = bodyReplaced,
                AttachmentFilePath = "",
                AttachmentFileName = "",
                AttachedDownloadId = "",
                CreatedOnUtc = DateTime.UtcNow,
                EmailAccountId = emailAccount.Id,
            };

            _queuedEmailService.InsertQueuedEmail(email);

            //activity log
            _customerActivityService.InsertActivity("CustomerReminder.AbandonedCart", customer.Id, _localizationService.GetResource("ActivityLog.AbandonedCart"), customerReminder.Name);

            return true;

        }

        #region Conditions
        protected bool CheckConditions(CustomerReminder customerReminder, Customer customer)
        {
            if(customerReminder.Conditions.Count == 0)
                return true;


            bool cond = false;
            foreach (var item in customerReminder.Conditions)
            {
                if(item.ConditionType == CustomerReminderConditionTypeEnum.Category)
                {
                    cond = ConditionCategory(item, customer.ShoppingCartItems.Select(x => x.ProductId).ToList());
                }
                if (item.ConditionType == CustomerReminderConditionTypeEnum.Product)
                {
                    cond = ConditionProducts(item, customer.ShoppingCartItems.Select(x=>x.ProductId).ToList());
                }
                if (item.ConditionType == CustomerReminderConditionTypeEnum.Manufacturer)
                {
                    cond = ConditionManufacturer(item, customer.ShoppingCartItems.Select(x => x.ProductId).ToList());
                }
                if (item.ConditionType == CustomerReminderConditionTypeEnum.CustomerTag)
                {
                    cond = ConditionCustomerTag(item, customer);
                }
                if (item.ConditionType == CustomerReminderConditionTypeEnum.CustomerRole)
                {
                    cond = ConditionCustomerRole(item, customer);
                }
                if (item.ConditionType == CustomerReminderConditionTypeEnum.CustomerRegisterField)
                {
                    cond = ConditionCustomerRegister(item, customer);
                }
                if (item.ConditionType == CustomerReminderConditionTypeEnum.CustomCustomerAttribute)
                {
                    cond = ConditionCustomerAttribute(item, customer);
                }
            }

            return cond;
        }
        protected bool ConditionCategory(CustomerReminder.ReminderCondition condition, ICollection<string> products)
        {
            bool cond = false;
            if (condition.Condition == CustomerReminderConditionEnum.AllOfThem)
            {
                cond = true;
                foreach (var item in condition.Categories)
                {
                    foreach (var product in products)
                    {
                        var pr = _productService.GetProductById(product);
                        if (pr != null)
                        {
                            if (pr.ProductCategories.Where(x => x.CategoryId == item).Count() == 0)
                                return false;
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
            }

            if (condition.Condition == CustomerReminderConditionEnum.OneOfThem)
            {
                foreach (var item in condition.Categories)
                {
                    foreach (var product in products)
                    {
                        var pr = _productService.GetProductById(product);
                        if (pr != null)
                        {
                            if (pr.ProductCategories.Where(x => x.CategoryId == item).Count() > 0)
                                return true;
                        }                       
                    }
                }
            }

            return cond;
        }
        protected bool ConditionManufacturer(CustomerReminder.ReminderCondition condition, ICollection<string> products)
        {
            bool cond = false;
            if (condition.Condition == CustomerReminderConditionEnum.AllOfThem)
            {
                cond = true;
                foreach (var item in condition.Manufacturers)
                {
                    foreach (var product in products)
                    {
                        var pr = _productService.GetProductById(product);
                        if (pr != null)
                        {
                            if (pr.ProductManufacturers.Where(x => x.ManufacturerId == item).Count() == 0)
                                return false;
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
            }

            if (condition.Condition == CustomerReminderConditionEnum.OneOfThem)
            {
                foreach (var item in condition.Manufacturers)
                {
                    foreach (var product in products)
                    {
                        var pr = _productService.GetProductById(product);
                        if (pr != null)
                        {
                            if (pr.ProductManufacturers.Where(x => x.ManufacturerId == item).Count() > 0)
                                return true;
                        }
                    }
                }
            }

            return cond;
        }
        protected bool ConditionProducts(CustomerReminder.ReminderCondition condition, ICollection<string> products)
        {
            bool cond = true;
            if (condition.Condition == CustomerReminderConditionEnum.AllOfThem)
            {
                cond = products.ContainsAll(condition.Products);
            }
            if (condition.Condition == CustomerReminderConditionEnum.OneOfThem)
            {
                cond = products.ContainsAny(condition.Products);
            }

            return cond;
        }
        protected bool ConditionCustomerRole(CustomerReminder.ReminderCondition condition, Customer customer)
        {
            bool cond = false;
            if (customer != null)
            {
                var customerRoles = customer.CustomerRoles;
                if (condition.Condition == CustomerReminderConditionEnum.AllOfThem)
                {
                    cond = customerRoles.Select(x => x.Id).ContainsAll(condition.CustomerRoles);
                }
                if (condition.Condition == CustomerReminderConditionEnum.OneOfThem)
                {
                    cond = customerRoles.Select(x => x.Id).ContainsAny(condition.CustomerRoles);
                }
            }
            return cond;
        }
        protected bool ConditionCustomerTag(CustomerReminder.ReminderCondition condition, Customer customer)
        {
            bool cond = false;
            if (customer != null)
            {
                var customerTags = customer.CustomerTags;
                if (condition.Condition == CustomerReminderConditionEnum.AllOfThem)
                {
                    cond = customerTags.Select(x => x).ContainsAll(condition.CustomerTags);
                }
                if (condition.Condition == CustomerReminderConditionEnum.OneOfThem)
                {
                    cond = customerTags.Select(x => x).ContainsAny(condition.CustomerTags);
                }
            }
            return cond;
        }
        protected bool ConditionCustomerRegister(CustomerReminder.ReminderCondition condition, Customer customer)
        {
            bool cond = false;
            if (customer != null)
            {
                if (condition.Condition == CustomerReminderConditionEnum.AllOfThem)
                {
                    cond = true;
                    foreach (var item in condition.CustomerRegistration)
                    {
                        if (customer.GenericAttributes.Where(x => x.Key == item.RegisterField && x.Value == item.RegisterValue).Count() == 0)
                            cond = false;
                    }
                }
                if (condition.Condition == CustomerReminderConditionEnum.OneOfThem)
                {
                    foreach (var item in condition.CustomerRegistration)
                    {
                        if (customer.GenericAttributes.Where(x => x.Key == item.RegisterField && x.Value == item.RegisterValue).Count() > 0)
                            cond = true;
                    }
                }
            }
            return cond;
        }
        protected bool ConditionCustomerAttribute(CustomerReminder.ReminderCondition condition, Customer customer)
        {
            bool cond = false;
            if (customer != null)
            {
                if (condition.Condition == CustomerReminderConditionEnum.AllOfThem)
                {
                    var customCustomerAttributes = customer.GenericAttributes.FirstOrDefault(x => x.Key == "CustomCustomerAttributes");
                    if (customCustomerAttributes != null)
                    {
                        if (!String.IsNullOrEmpty(customCustomerAttributes.Value))
                        {
                            var selectedValues = _customerAttributeParser.ParseCustomerAttributeValues(customCustomerAttributes.Value);
                            cond = true;
                            foreach (var item in condition.CustomCustomerAttributes)
                            {
                                var _fields = item.RegisterField.Split(':');
                                if (_fields.Count() > 1)
                                {
                                    if (selectedValues.Where(x => x.CustomerAttributeId == _fields.FirstOrDefault() && x.Id == _fields.LastOrDefault()).Count() == 0)
                                        cond = false;
                                }
                                else
                                    cond = false;
                            }
                        }
                    }
                }
                if (condition.Condition == CustomerReminderConditionEnum.OneOfThem)
                {

                    var customCustomerAttributes = customer.GenericAttributes.FirstOrDefault(x => x.Key == "CustomCustomerAttributes");
                    if (customCustomerAttributes != null)
                    {
                        if (!String.IsNullOrEmpty(customCustomerAttributes.Value))
                        {
                            var selectedValues = _customerAttributeParser.ParseCustomerAttributeValues(customCustomerAttributes.Value);
                            foreach (var item in condition.CustomCustomerAttributes)
                            {
                                var _fields = item.RegisterField.Split(':');
                                if (_fields.Count() > 1)
                                {
                                    if (selectedValues.Where(x => x.CustomerAttributeId == _fields.FirstOrDefault() && x.Id == _fields.LastOrDefault()).Count() > 0)
                                        cond = true;
                                }
                            }
                        }
                    }
                }
            }
            return cond;
        }
        #endregion

        protected void UpdateHistory(Customer customer, CustomerReminder customerReminder, string reminderlevelId, CustomerReminderHistory history)
        {
            if(history!=null)
            {
                history.Levels.Add(new CustomerReminderHistory.HistoryLevel()
                {
                    Level = customerReminder.Levels.FirstOrDefault(x => x.Id == reminderlevelId).Level,
                    ReminderLevelId = reminderlevelId,
                    SendDate = DateTime.UtcNow,
                });
                if(customerReminder.Levels.Max(x=>x.Level) == 
                    customerReminder.Levels.FirstOrDefault(x => x.Id == reminderlevelId).Level)
                {
                    history.Status = (int)CustomerReminderHistoryStatusEnum.CompletedReminder;
                    history.EndDate = DateTime.UtcNow;
                }
                _customerReminderHistoryRepository.Update(history);
            }
            else
            {
                history = new CustomerReminderHistory();
                history.CustomerId = customer.Id;
                history.Status = (int)CustomerReminderHistoryStatusEnum.Started;
                history.StartDate = DateTime.UtcNow;
                history.CustomerReminderId = customerReminder.Id;
                history.ReminderRuleId = customerReminder.ReminderRuleId;
                history.Levels.Add(new CustomerReminderHistory.HistoryLevel()
                {
                    Level = customerReminder.Levels.FirstOrDefault(x => x.Id == reminderlevelId).Level,
                    ReminderLevelId = reminderlevelId,
                    SendDate = DateTime.UtcNow,
                });

                _customerReminderHistoryRepository.Insert(history);
            }

        }

        protected void CloseHistoryReminder(Customer customer, CustomerReminder customerReminder, CustomerReminderHistory history)
        {
            history.Status = (int)CustomerReminderHistoryStatusEnum.CompletedReminder;
            history.EndDate = DateTime.UtcNow;
            _customerReminderHistoryRepository.Update(history);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Gets customer reminder
        /// </summary>
        /// <param name="id">Customer reminder identifier</param>
        /// <returns>Customer reminder</returns>
        public virtual CustomerReminder GetCustomerReminderById(string id)
        {
            return _customerReminderRepository.GetById(id);
        }


        /// <summary>
        /// Gets all customer reminders
        /// </summary>
        /// <returns>Customer reminders</returns>
        public virtual IList<CustomerReminder> GetCustomerReminders()
        {
            var query = from p in _customerReminderRepository.Table
                        orderby p.DisplayOrder
                        select p;
            return query.ToList();
        }

        /// <summary>
        /// Inserts a customer reminder
        /// </summary>
        /// <param name="CustomerReminder">Customer reminder</param>
        public virtual void InsertCustomerReminder(CustomerReminder customerReminder)
        {
            if (customerReminder == null)
                throw new ArgumentNullException("customerReminder");

            _customerReminderRepository.Insert(customerReminder);

            //event notification
            _eventPublisher.EntityInserted(customerReminder);

        }

        /// <summary>
        /// Delete a customer reminder
        /// </summary>
        /// <param name="customerReminder">Customer reminder</param>
        public virtual void DeleteCustomerReminder(CustomerReminder customerReminder)
        {
            if (customerReminder == null)
                throw new ArgumentNullException("customerReminder");

            _customerReminderRepository.Delete(customerReminder);

            //event notification
            _eventPublisher.EntityDeleted(customerReminder);

        }

        /// <summary>
        /// Updates the customer reminder
        /// </summary>
        /// <param name="CustomerReminder">Customer reminder</param>
        public virtual void UpdateCustomerReminder(CustomerReminder customerReminder)
        {
            if (customerReminder == null)
                throw new ArgumentNullException("customerReminder");

            _customerReminderRepository.Update(customerReminder);

            //event notification
            _eventPublisher.EntityUpdated(customerReminder);
        }


        /// <summary>
        /// Get allowed tokens for rule
        /// </summary>
        /// <param name="Rule">Customer Reminder Rule</param>
        public string[] AllowedTokens(CustomerReminderRuleEnum rule)
        {
            var allowedTokens = new List<string>();
            allowedTokens.AddRange(
                new List<string>{ "%Store.Name%",
                "%Store.URL%",
                "%Store.Email%",
                "%Store.CompanyName%",
                "%Store.CompanyAddress%",
                "%Store.CompanyPhoneNumber%",
                "%Store.CompanyVat%",
                "%Twitter.URL%",
                "%Facebook.URL%",
                "%YouTube.URL%",
                "%GooglePlus.URL%"}
                );

            if(rule == CustomerReminderRuleEnum.AbandonedCart)
            {
                allowedTokens.Add("%Cart%");

            }
            allowedTokens.AddRange(
                new List<string>{
                "%Customer.Email%",
                "%Customer.Username%",
                "%Customer.FullName%",
                "%Customer.FirstName%",
                "%Customer.LastName%"
                });
            return allowedTokens.ToArray();
        }

        #endregion

        #region Tasks

        public virtual void Task_AbandonedCart()
        {
            var datetimeUtcNow = DateTime.UtcNow;
            var customerReminder = (from cr in _customerReminderRepository.Table
                                   where cr.Active && datetimeUtcNow >= cr.StartDateTimeUtc && datetimeUtcNow <= cr.EndDateTimeUtc
                                    select cr).ToList();

            if (customerReminder.Count > 0)
            {
                foreach (var reminder in customerReminder)
                {
                    var customers = from cu in _customerRepository.Table
                                    where cu.HasShoppingCartItems && cu.LastUpdateCartDateUtc > reminder.LastUpdateDate
                                    && (!String.IsNullOrEmpty(cu.Email))
                                    select cu;

                    foreach (var customer in customers)
                    {
                        var history = (from hc in _customerReminderHistoryRepository.Table
                                             where hc.CustomerId == customer.Id && hc.CustomerReminderId == reminder.Id                                             
                                             select hc).ToList();
                        if(history.Count > 0)
                        {
                            var activereminderhistory = history.FirstOrDefault(x => x.HistoryStatus == CustomerReminderHistoryStatusEnum.Started);
                            if (activereminderhistory != null)
                            {
                                var lastLevel = activereminderhistory.Levels.OrderBy(x => x.SendDate).LastOrDefault();
                                var reminderLevel = reminder.Levels.FirstOrDefault(x => x.Level > lastLevel.Level);
                                if(reminderLevel!=null)
                                {
                                    if (DateTime.UtcNow > lastLevel.SendDate.AddDays(reminderLevel.Day).AddHours(reminderLevel.Hour))
                                    {
                                        var send = SendEmail_AbandonedCart(customer, reminder, reminderLevel.Id);
                                        if (send)
                                            UpdateHistory(customer, reminder, reminderLevel.Id, activereminderhistory);
                                    }
                                }
                                else
                                {
                                    CloseHistoryReminder(customer, reminder, activereminderhistory);
                                }
                            }
                            else
                            {
                                if(DateTime.UtcNow > history.Max(x=>x.EndDate).AddDays(reminder.RenewedDay) && reminder.AllowRenew)
                                {
                                    var level = reminder.Levels.OrderBy(x => x.Level).FirstOrDefault() != null ? reminder.Levels.OrderBy(x => x.Level).FirstOrDefault() : null;
                                    if (level!=null)
                                    {

                                        if (DateTime.UtcNow > customer.LastUpdateCartDateUtc.Value.AddDays(level.Day).AddHours(level.Hour))
                                        {
                                            if (CheckConditions(reminder, customer))
                                            {
                                                var send = SendEmail_AbandonedCart(customer, reminder, level.Id);
                                                if (send)
                                                    UpdateHistory(customer, reminder, level.Id, null);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            var level = reminder.Levels.OrderBy(x => x.Level).FirstOrDefault() != null ? reminder.Levels.OrderBy(x => x.Level).FirstOrDefault() : null;
                            if (level != null)
                            {

                                if (DateTime.UtcNow > customer.LastUpdateCartDateUtc.Value.AddDays(level.Day).AddHours(level.Hour))
                                {
                                    if (CheckConditions(reminder, customer))
                                    {
                                        var send = SendEmail_AbandonedCart(customer, reminder, level.Id);
                                        if (send)
                                            UpdateHistory(customer, reminder, level.Id, null);
                                    }
                                }
                            }
                        }
                    }
                }
            }


        }

        #endregion
    }
}
