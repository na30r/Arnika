import { AxiosRequestConfig } from 'axios';
import { Company } from '../entities/Company';
import APIClient, { FetchResponse } from './api-client';
import { sample_companies1 } from '../sampleData/sample-companies1';

const url = 'data';
class CompanyApi extends APIClient<Company> {
  constructor() {
    super(url);
  }
  getAll = (config: AxiosRequestConfig) => {
    return Promise.resolve({
      count: sample_companies1.length,
      next: null,
      results: sample_companies1,
    });
  };

  getAllbyIds = (config: AxiosRequestConfig, companyIds: number[] | null) => {
    if (companyIds === null || companyIds === null) {
      return this.getAll(config);
    }
    const filtered = sample_companies1.filter((company) => companyIds.includes(company.id));
    return Promise.resolve({
      count: filtered.length,
      next: null,
      results: filtered,
    });
  };
}
export default CompanyApi;
