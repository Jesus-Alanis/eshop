﻿using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Microsoft.Extensions.Configuration;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace Microsoft.eShopWeb.ApplicationCore.Services
{
    public class OrderService : IOrderService
    {
        private readonly IRepository<Order> _orderRepository;
        private readonly IUriComposer _uriComposer;
        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;
        private readonly IRepository<Basket> _basketRepository;
        private readonly IRepository<CatalogItem> _itemRepository;

        public OrderService(IRepository<Basket> basketRepository,
            IRepository<CatalogItem> itemRepository,
            IRepository<Order> orderRepository,
            IUriComposer uriComposer,
            IConfiguration configuration,
            HttpClient httpClient)
        {
            _orderRepository = orderRepository;
            _uriComposer = uriComposer;
            _configuration = configuration;
            _httpClient = httpClient;
            _basketRepository = basketRepository;
            _itemRepository = itemRepository;
        }

        public async Task CreateOrderAsync(int basketId, Address shippingAddress)
        {
            var basketSpec = new BasketWithItemsSpecification(basketId);
            var basket = await _basketRepository.GetBySpecAsync(basketSpec);

            Guard.Against.NullBasket(basketId, basket);
            Guard.Against.EmptyBasketOnCheckout(basket.Items);

            var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
            var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

            var items = basket.Items.Select(basketItem =>
            {
                var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
                var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
                var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
                return orderItem;
            }).ToList();

            var order = new Order(basket.BuyerId, shippingAddress, items);

            var orderItems = items.Select(i => new { ItemId = i.ItemOrdered.CatalogItemId, Quantity = i.Units }).ToList();
            var sbConnection = _configuration["ServiceBusConnection"];

            //TODO: inject service bus client
            await using var client = new ServiceBusClient(sbConnection);
            var sender = client.CreateSender(_configuration.GetValue<string>("QueueName"));

            var json = JsonSerializer.Serialize(orderItems);
            var message = new ServiceBusMessage(json) { ContentType = "application/json" };
            await sender.SendMessageAsync(message);

            await _orderRepository.AddAsync(order);

            var deliveryOrder = new
            {
                FinalPrice = order.Total(),
                Items = order.OrderItems,
                ShippingAddress = order.ShipToAddress
            };

            var deliveryorderUrl = _configuration.GetValue<string>("DeliveryOrderUrl");
            await _httpClient.PostAsJsonAsync($"{deliveryorderUrl}{_configuration["DeliveryOrderKey"]}", deliveryOrder);
        }
    }
}
