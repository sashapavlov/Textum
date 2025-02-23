﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using HtmlAgilityPack;
using Konsole;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Newtonsoft.Json;
using SerilogTimings;
using TextumReader.TranslationsCollectorWorkerService.Abstract;
using TextumReader.TranslationsCollectorWorkerService.Exceptions;
using TextumReader.TranslationsCollectorWorkerService.Models;
using TextumReader.TranslationsCollectorWorkerService.Services;

namespace TextumReader.TranslationsCollectorWorkerService.EventHandlers
{
    public class PlaywrightTranslationEventHandler : ITranslationEventHandler
    {
        private readonly IConfiguration _config;
        private readonly CognitiveServicesTranslator _cognitiveServicesTranslator;
        private readonly ILogger<PlaywrightTranslationEventHandler> _logger;
        private readonly ProxyProvider _proxyProvider;
        private readonly ServiceBusReceiver _receiver;
        private readonly TelemetryClient _telemetryClient;

        public PlaywrightTranslationEventHandler(
            ServiceBusReceiver receiver,
            TelemetryClient telemetryClient,
            CognitiveServicesTranslator cognitiveServicesTranslator,
            ProxyProvider proxyProvider,
            ILogger<PlaywrightTranslationEventHandler> logger,
            IConfiguration config)
        {
            _config = config;
            _receiver = receiver;
            _telemetryClient = telemetryClient;
            _cognitiveServicesTranslator = cognitiveServicesTranslator;
            _proxyProvider = proxyProvider;
            _logger = logger;
        }

        public async Task<List<TranslationEntity>> Handle(ServiceBusReceivedMessage message, CancellationToken stoppingToken)
        {
            _logger.LogInformation("PlaywrightTranslationEventHandler.Handle for message {id}", message.MessageId);

            using var handleEventOperation =
                _telemetryClient.StartOperation<DependencyTelemetry>("PlaywrightTranslationEventHandler.Handle");

            var (from, to, words) = JsonConvert.DeserializeObject<TranslationRequest>(message.Body.ToString());

            var translationEntities = new List<TranslationEntity>();

            var pb = new ProgressBar(PbStyle.SingleLine, words.Count);

            try
            {
                pb.Refresh(0, "Init...");

                using var playwright = await Playwright.CreateAsync();

                var options = new BrowserTypeLaunchOptions();

                if (_config.GetValue<bool>("UseProxy"))
                {
                    var(proxyUrl, port, login,  password) = _proxyProvider.GetProxy();

                    options.Proxy = new Proxy
                    {
                        Server = proxyUrl + ":" + port,
                        Username = login,
                        Password = password,
                        Bypass = "play.google.com,gstatic.com,www.gstatic.com"
                    };
                }

                options.Headless = _config.GetValue<bool>("Headless");
                
                await using (var browser = await playwright.Chromium.LaunchAsync(options))
                {
                    //var context = await browser.NewContextAsync();

                    var page = await browser.NewPageAsync();

                    page.SetDefaultNavigationTimeout(250000);

                    for (var i = 0; i < words.Count; i++)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            await browser.CloseAsync();
                            handleEventOperation.Telemetry.Success = false;

                            throw new ApplicationException("Cancellation requested");
                        }

                        if (DateTimeOffset.UtcNow > message.LockedUntil.AddMinutes(-2))
                        {
                            _logger.LogInformation("Lock is to be expired");
                            _telemetryClient.TrackEvent("Lock is to be expired");
                            await _receiver.RenewMessageLockAsync(message, stoppingToken);
                            _logger.LogInformation("Lock renewed");
                        }

                        var result = await GetTranslations(page, @from, to, words, i, options);

                        //_translationService.Insert(result);
                        translationEntities.Add(result);

                        pb.Refresh(i + 1, $"{words[i]}");
                    }
                }

                _logger.LogInformation("Scrapping complete");

                pb.Refresh(words.Count, "Scrapping complete");

                handleEventOperation.Telemetry.Success = true;

                return translationEntities;
            }
            catch (Exception e)
            {
                pb.Refresh(0, $"{e.Message}");
                handleEventOperation.Telemetry.Success = false;
                throw;
            }
        }

        private async Task<TranslationEntity> GetTranslations(IPage page, string @from, string to, IList<string> words, int i, BrowserTypeLaunchOptions chromeOptions)
        {
            using var oneWordProcessingOperation = _telemetryClient.StartOperation<DependencyTelemetry>("One word processing");

            using var oneWordProcessingOperationTiming = Operation.Begin("One word processing");

            try
            {
                using (Operation.Time("driver.Navigate().GoToUrl"))
                {
                    await page.GotoAsync($"https://translate.google.com/?sl={@from}&tl={to}&text={words[i]}&op=translate");
                }

                var result = await ProcessPage(words[i], @from, to, page, chromeOptions);

                if (result.Translations != null)
                {
                    var translations = result.Translations
                        .Where(t => t.Frequency == "Common" || t.Frequency == "Uncommon").ToList();

                    foreach (var trans in translations)
                        trans.Examples = _cognitiveServicesTranslator
                            .GetExamples(@from, to, result.Word, trans.Translation).Take(3);
                }

                oneWordProcessingOperation.Telemetry.Success = true;
                oneWordProcessingOperationTiming.Complete();
                return result;
            }
            catch (Exception e)
            {
                oneWordProcessingOperation.Telemetry.Success = false;
                oneWordProcessingOperationTiming.Cancel();
                throw;
            }
        }


        private async Task<TranslationEntity> ProcessPage(string word, string from, string to, IPage driver, BrowserTypeLaunchOptions chromeOptions)
        {
            using var processPageOperationTiming = Operation.Begin("PlaywrightTranslationEventHandler.ProcessPage");

            var element = await driver.QuerySelectorAsync(
                "button[class='VfPpkd-LgbsSe VfPpkd-LgbsSe-OWXEXe-k8QpJ VfPpkd-LgbsSe-OWXEXe-dgl2Hf nCP5yc AjY5Oe DuMIQc']");

            if (element != null)
            {
                if (await element.TextContentAsync() == "I agree")
                {
                    processPageOperationTiming.Cancel();
                    HandleProxyError(chromeOptions);
                }
            }

            var errorEl = await driver.QuerySelectorAsync("div[class='QGDZGb']");
            if (errorEl != null && await errorEl.IsVisibleAsync())
            {
                var text = await errorEl.TextContentAsync();

                if (text == "Translation error")
                {
                    _logger.LogError("IP is compromised {proxy}", chromeOptions.Proxy?.Server);

                    processPageOperationTiming.Cancel();
                    HandleProxyError(chromeOptions);
                }
            }


            var mainTranslationEl = await driver.WaitForSelectorAsync(".VIiyi");

            string mainTranslation = await mainTranslationEl?.InnerTextAsync();

            // if translations present
            var docEl = await driver.QuerySelectorAsync("div[class='I87fLc oLovEc XzOhkf']");

            var doc = "";

            if (docEl != null)
            {
                doc = await docEl.InnerHTMLAsync();
            }
            else
            {
                // if no translations except main

                //_logger.LogInformation("Finished getting translations for {word}", word);

                processPageOperationTiming.Complete();

                return new TranslationEntity
                {
                    Word = word,
                    MainTranslation = mainTranslation,
                    From = from,
                    To = to,
                    Id = $"{from}-{to}-{word}"
                };
            }

            var html = new HtmlDocument();
            html.LoadHtml(doc);

            var nodes2 = html.DocumentNode.SelectNodes("//div/table/tbody/tr");

            var rows = nodes2.Select(tr =>
            {
                var thElements = tr.Elements("th").Select(el => el.InnerText.Trim());
                var tdElements = tr.Elements("td").Select(el => el.InnerText.Trim());

                return thElements.Concat(tdElements).ToArray();
            }).ToArray();

            var result = new List<WordTranslation>();
            var currentPartOfSpeach = "";
            foreach (var t in rows)
            {
                if (t.Length == 4)
                {
                    currentPartOfSpeach = t[0];
                    result.Add(new WordTranslation
                    {
                        PartOfSpeech = currentPartOfSpeach,
                        Translation = t[1],
                        Synonyms = t[2].Split(","),
                        Frequency = t[3]
                    });
                }

                if (t.Length == 3)
                    result.Add(new WordTranslation
                    {
                        PartOfSpeech = currentPartOfSpeach,
                        Translation = t[0],
                        Synonyms = t[1].Split(","),
                        Frequency = t[2]
                    });
            }

            //_logger.LogInformation("Finished getting translations for {word}", word);

            processPageOperationTiming.Complete();

            return new TranslationEntity
            {
                Word = word,
                From = from,
                To = to,
                MainTranslation = mainTranslation,
                Translations = result,
                Id = $"{from}-{to}-{word}"
            };
        }

        private void HandleProxyError(BrowserTypeLaunchOptions chromeOptions)
        {
            _logger.LogError("IP is compromised {chromeOptions.Proxy.HttpProxy}", chromeOptions.Proxy?.Server);
            if (_config.GetValue<bool>("UseProxy"))
            {
                _proxyProvider.ExcludeProxy(
                    $"{chromeOptions.Proxy?.Server}:{chromeOptions.Proxy?.Username}:{chromeOptions.Proxy?.Password}");
            }

            throw new CompromisedException($"IP is compromised {chromeOptions?.Proxy?.Server ?? "local"}");
        }
    }
}