﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using TextumReader.TranslationJobProcessor.Models;

namespace TextumReader.TranslationJobProcessor.Abstract
{
    public interface ITranslationEventHandler
    {
        Task<List<TranslationEntity>> Handle(ServiceBusReceivedMessage m);
    }
}
