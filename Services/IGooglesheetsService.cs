﻿using Google.Apis.Sheets.v4;

namespace GuitoApi.Services
{
    public interface IGooglesheetsService
    {
        public Task<SheetsService> Get();
    }
}
